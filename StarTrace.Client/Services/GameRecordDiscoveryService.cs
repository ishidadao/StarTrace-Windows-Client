using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace StarTrace.Client.Services;

public sealed class GameRecordDiscoveryService
{
    private const long MaxBytesPerFile = 64L * 1024 * 1024;
    private static readonly Regex UrlPattern = new(@"https?://[^\0\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DataDirectoryPattern = new(@"[A-Za-z]:[\\/][^\r\n""]+?(?:GenshinImpact|YuanShen)_Data", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GenshinHistoryUrlPattern = new(
        @"https?://[^\0\r\n""'<>]*?webstatic\.mihoyo\.com/[^\0\r\n""'<>]*?game_biz=hk4e_(?:global|cn)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GenshinApiUrlPattern = new(
        @"https?://[^\0\r\n""'<>]*?getGachaLog\?[^\0\r\n""'<>]*?game_biz=hk4e_(?:global|cn)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string> DiscoverAsync(string game, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (game is not ("genshin" or "wuwa")) throw new ArgumentException("不支持的游戏来源。");
        progress?.Report("正在定位游戏进程、日志与网页缓存…");

        var candidates = (game == "genshin" ? FindGenshinFiles() : FindWutheringWavesFiles())
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        if (candidates.Count == 0)
            throw new FileNotFoundException(game == "genshin"
                ? "未找到原神日志或网页缓存。请先进入游戏并打开一次祈愿历史。"
                : "未找到鸣潮日志或网页缓存。请先进入游戏并打开一次唤取记录。");

        foreach (var file in candidates)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"正在检查 {file.Name}…");
            var urls = await ExtractUrlsAsync(file.FullName, game, token);
            var match = urls.LastOrDefault(url => IsRecordUrl(game, url));
            if (!string.IsNullOrWhiteSpace(match)) return match;
        }

        throw new InvalidOperationException(game == "genshin"
            ? "缓存中没有可用的原神授权地址。请在游戏里重新打开祈愿历史后立即重试。"
            : "本次会话日志中没有可用的鸣潮唤取地址。请保持游戏运行，重新打开唤取记录页，等待完整显示后立即重试。");
    }

    private static IEnumerable<string> FindGenshinFiles()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "miHoYo");
        AddFile(paths, Path.Combine(localLow, "Genshin Impact", "output_log.txt"));
        AddFile(paths, Path.Combine(localLow, "原神", "output_log.txt"));

        foreach (var processName in new[] { "GenshinImpact", "YuanShen" })
        {
            foreach (var executable in RunningExecutables(processName))
            {
                var gameRoot = Path.GetDirectoryName(executable);
                if (gameRoot is null) continue;
                AddCacheFiles(paths, Path.Combine(gameRoot, processName + "_Data", "webCaches"));
            }
        }

        foreach (var drive in FixedDriveRoots())
        {
            foreach (var relative in new[]
            {
                @"Program Files\HoYoPlay\games\Genshin Impact game\GenshinImpact_Data\webCaches",
                @"Program Files\Genshin Impact\Genshin Impact game\GenshinImpact_Data\webCaches",
                @"HoYoPlay\games\Genshin Impact game\GenshinImpact_Data\webCaches",
                @"Genshin Impact\Genshin Impact game\GenshinImpact_Data\webCaches",
                @"miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\webCaches",
                @"原神\Genshin Impact Game\YuanShen_Data\webCaches",
            }) AddCacheFiles(paths, Path.Combine(drive, relative));
        }

        foreach (var log in paths.Where(path => Path.GetFileName(path).Equals("output_log.txt", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            foreach (var root in ExtractDataDirectories(log)) AddCacheFiles(paths, Path.Combine(root, "webCaches"));
        }
        return paths;
    }

    private static IEnumerable<string> FindWutheringWavesFiles()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddLogFiles(paths, Path.Combine(local, "Wuthering Waves", "Saved", "Logs"));
        AddLogFiles(paths, Path.Combine(local, "WutheringWaves", "Saved", "Logs"));

        foreach (var executable in RunningExecutables("Client-Win64-Shipping"))
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
            while (directory.Parent is not null && !directory.Name.Equals("Client", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
            if (directory.Name.Equals("Client", StringComparison.OrdinalIgnoreCase))
            {
                AddLogFiles(paths, Path.Combine(directory.FullName, "Saved", "Logs"));
                AddCacheFiles(paths, Path.Combine(directory.FullName, "Saved", "WebView"));
            }
        }

        foreach (var drive in FixedDriveRoots())
        {
            foreach (var relative in new[]
            {
                @"Wuthering Waves\Wuthering Waves Game\Client\Saved",
                @"Wuthering Waves Game\Client\Saved",
                @"Program Files\Wuthering Waves\Wuthering Waves Game\Client\Saved",
                @"Program Files\Epic Games\WutheringWavesj3oFh\Client\Saved",
                @"Program Files\WeGameApps\rail_apps\Wuthering Waves(2002122)\Client\Saved",
                @"SteamLibrary\steamapps\common\Wuthering Waves\Wuthering Waves Game\Client\Saved",
                @"Program Files (x86)\Steam\steamapps\common\Wuthering Waves\Wuthering Waves Game\Client\Saved",
                @"Program Files\Steam\steamapps\common\Wuthering Waves\Wuthering Waves Game\Client\Saved",
                @"鸣潮\Wuthering Waves Game\Client\Saved",
            })
            {
                var saved = Path.Combine(drive, relative);
                AddLogFiles(paths, Path.Combine(saved, "Logs"));
                AddCacheFiles(paths, Path.Combine(saved, "WebView"));
            }
        }
        return paths;
    }

    private static IEnumerable<string> RunningExecutables(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool usable;
            try { usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; } catch { usable = false; }
            if (usable) yield return drive.RootDirectory.FullName;
        }
    }

    private static void AddFile(ISet<string> paths, string path)
    {
        try { if (File.Exists(path)) paths.Add(Path.GetFullPath(path)); } catch { }
    }

    private static void AddLogFiles(ISet<string> paths, string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "*.log", SearchOption.TopDirectoryOnly)) paths.Add(path);
        }
        catch { }
    }

    private static void AddCacheFiles(ISet<string> paths, string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "data_2", SearchOption.AllDirectories)) paths.Add(path);
            foreach (var path in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)) paths.Add(path);
        }
        catch { }
    }

    private static IEnumerable<string> ExtractDataDirectories(string logPath)
    {
        string text;
        try { text = File.ReadAllText(logPath); } catch { yield break; }
        foreach (Match match in DataDirectoryPattern.Matches(text))
        {
            var path = match.Value.Replace('/', Path.DirectorySeparatorChar).Trim('"');
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static async Task<List<string>> ExtractUrlsAsync(string path, string game, CancellationToken token)
    {
        var result = new List<string>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, true);
        if (stream.Length > MaxBytesPerFile) stream.Seek(-MaxBytesPerFile, SeekOrigin.End);
        var buffer = new byte[1024 * 1024];
        var rawCarry = "";
        var decodedCarry = "";
        var decodeClientLog = game == "wuwa"
            && Path.GetFileName(path).Equals("Client.log", StringComparison.OrdinalIgnoreCase);
        var scanGenshinCache = game == "genshin"
            && Path.GetFileName(path).Equals("data_2", StringComparison.OrdinalIgnoreCase);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            var rawText = rawCarry + DecodeLogText(buffer, read);
            CollectUrls(rawText, result);
            if (scanGenshinCache) CollectGenshinCacheUrls(rawText, result);
            rawCarry = Tail(rawText);

            if (decodeClientLog)
            {
                var decoded = buffer.AsSpan(0, read).ToArray();
                DecodeWutheringWavesClientLog(decoded);
                var decodedText = decodedCarry + DecodeLogText(decoded, decoded.Length);
                CollectUrls(decodedText, result);
                decodedCarry = Tail(decodedText);
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string DecodeLogText(byte[] bytes, int length) => Encoding.UTF8.GetString(bytes, 0, length)
        .Replace("\\/", "/")
        .Replace("\\u0026", "&")
        .Replace("\\u003d", "=")
        .Replace("\\u003f", "?");

    private static string Tail(string text) => text.Length > 16_384 ? text[^16_384..] : text;

    private static void CollectUrls(string text, ICollection<string> result)
    {
        foreach (var scan in new[] { text, text.Replace("\0", "") })
        {
            foreach (Match match in UrlPattern.Matches(scan))
            {
                var normalized = NormalizeUrl(match.Value);
                if (normalized is not null) result.Add(normalized);
            }
        }
    }

    private static void CollectGenshinCacheUrls(string text, ICollection<string> result)
    {
        // Chromium's data_2 file is binary. The ordinary whitespace-oriented URL
        // matcher can consume the following cache record and create an invalid
        // mega-URL, so use the same bounded game_biz terminator as the widely used
        // local Paimon/Teyvat helper scripts. No remote script is executed.
        foreach (var pattern in new[] { GenshinApiUrlPattern, GenshinHistoryUrlPattern })
        {
            foreach (Match match in pattern.Matches(text))
            {
                var normalized = NormalizeUrl(match.Value);
                if (normalized is null) continue;
                if (pattern == GenshinHistoryUrlPattern && !normalized.EndsWith("#/log", StringComparison.OrdinalIgnoreCase))
                    normalized += "#/log";
                result.Add(normalized);
            }
        }
    }

    private static void DecodeWutheringWavesClientLog(Span<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            bytes[index] = (byte)(value ^ ((value & 1) != 0 ? 0xA5 : 0xEF));
        }
    }

    private static string? NormalizeUrl(string source)
    {
        var value = WebUtility.HtmlDecode(source).TrimEnd(')', ']', '}', ',', ';', '\\', '\0');
        // Do not unescape a valid URL as a whole. In particular, turning %2B
        // inside authkey into '+' makes the later form-style query decoder turn
        // it into a space and invalidates the signature.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return value;
        try { value = Uri.UnescapeDataString(value); } catch { return null; }
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" ? value : null;
    }

    private static bool IsRecordUrl(string game, string url) => game == "genshin"
        ? url.Contains("authkey=", StringComparison.OrdinalIgnoreCase)
          && (url.Contains("getGachaLog", StringComparison.OrdinalIgnoreCase)
              || url.Contains("webstatic.mihoyo.com", StringComparison.OrdinalIgnoreCase)
                 && url.Contains("game_biz=hk4e_", StringComparison.OrdinalIgnoreCase))
        : url.Contains("player_id=", StringComparison.OrdinalIgnoreCase)
          && (url.Contains("resources_id=", StringComparison.OrdinalIgnoreCase)
              || url.Contains("gacha_id=", StringComparison.OrdinalIgnoreCase))
          && url.Contains("svr_id=", StringComparison.OrdinalIgnoreCase)
          && url.Contains("record_id=", StringComparison.OrdinalIgnoreCase);
}
