using System.Text;

namespace ScContractTracker.Core.Logs;

/// <summary>
/// Sleduje Game.log jako "tail -f". Hra soubor drží otevřený, proto čteme s FileShare.ReadWrite.
/// Při spuštění hry se log přesune do logbackups a začne nový – to poznáme podle zkrácení souboru.
/// </summary>
public sealed class GameLogWatcher : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly StringBuilder _partial = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _position;

    public GameLogWatcher(string path, TimeSpan? interval = null)
    {
        _path = path;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    public string Path => _path;

    /// <summary>Nový kompletní řádek logu.</summary>
    public event Action<string>? LineRead;

    /// <summary>Log byl zkrácen/nahrazen – hra byla znovu spuštěna.</summary>
    public event Action? LogRestarted;

    public event Action<Exception>? Error;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <param name="fromStart">true = přečte i existující obsah (obnova stavu aktuální relace).</param>
    public void Start(bool fromStart = true)
    {
        Stop();
        _position = fromStart ? 0 : (File.Exists(_path) ? new FileInfo(_path).Length : 0);
        _partial.Clear();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignorováno */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error?.Invoke(ex);
            }
            try { await Task.Delay(_interval, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Jedno přečtení nových dat. Veřejné kvůli testům a ručnímu vynucení.</summary>
    public void Poll()
    {
        if (!File.Exists(_path)) return;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _position)
        {
            _position = 0;
            _partial.Clear();
            LogRestarted?.Invoke();
        }
        if (fs.Length == _position) return;

        fs.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            _position += read;
            var count = decoder.GetChars(buffer, 0, read, chars, 0);
            for (var i = 0; i < count; i++)
            {
                var c = chars[i];
                if (c == '\n')
                {
                    var line = _partial.ToString().TrimEnd('\r');
                    _partial.Clear();
                    if (line.Length > 0) LineRead?.Invoke(line);
                }
                else
                {
                    _partial.Append(c);
                }
            }
        }
    }

    /// <summary>Přečte celý (i archivní) log najednou – pro import historie z logbackups.</summary>
    public static IEnumerable<string> ReadAllLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        while (sr.ReadLine() is { } line) yield return line;
    }

    public void Dispose() => Stop();
}
