using ScContractTracker.Core.Imaging;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Tracking;

namespace ScContractTracker.App.Services;

public enum MobiGlasState
{
    Disabled,
    Idle,
    /// <summary>Po záznamu o kontraktu v logu čekáme, až hráč otevře Contracts › Accepted.</summary>
    Watching,
    AcceptedRead,
    /// <summary>Obraz hry nejde zachytit (černý) – je potřeba snímek obrazovky ze hry.</summary>
    CaptureBlank,
    /// <summary>Snímek obrazovky ze hry nebyl záložka Accepted.</summary>
    ScreenshotIgnored
}

/// <summary>
/// Po živém záznamu o kontraktu v Game.logu (přijetí, dokončení, opuštění, cíl…) opakovaně snímá okno hry a pomocí OCR čeká,
/// až se zobrazí aplikace Contracts se záložkou Accepted. Pak přečte seznam a detail kontraktu.
/// Dokud je záložka vidět, sledování se prodlužuje (hráč může proklikat další kontrakty).
/// Stejné zpracování se použije i pro snímky obrazovky pořízené hrou.
/// </summary>
public sealed class MobiGlasMonitor : IDisposable
{
    private readonly WindowsOcrService _ocr;
    private readonly MobiGlasParser _parser;
    private readonly ContractTracker _tracker;
    private readonly CaptureService _capture;
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private CancellationTokenSource? _session;
    private readonly object _lock = new();
    private DateTime _deadline;
    private bool _running;
    private bool _force;

    // Úspora OCR: výřez jen s aplikací Contracts a přeskočení nezměněných snímků
    private (double X, double Y, double W, double H)? _crop;
    private (int W, int H) _cropFrameSize;
    private byte[]? _lastSignature;
    private MobiGlasSnapshot? _lastSnapshot;

    /// <summary>Počet snímků, které šly do OCR / byly přeskočeny (stejný obraz) – pro diagnostiku.</summary>
    public int OcrRuns { get; private set; }
    public int OcrSkipped { get; private set; }

    public MobiGlasMonitor(WindowsOcrService ocr, MobiGlasParser parser, ContractTracker tracker, CaptureService capture)
    {
        _ocr = ocr;
        _parser = parser;
        _tracker = tracker;
        _capture = capture;
    }

    public bool Enabled { get; set; } = true;
    public TimeSpan WatchDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(1200);

    public MobiGlasState State { get; private set; } = MobiGlasState.Idle;
    public int LastEntryCount { get; private set; }
    public string? LastSource { get; private set; }

    public event Action<MobiGlasState>? StateChanged;

    /// <summary>Poslední snímek, na kterém byla vidět aplikace Contracts – pro diagnostiku OCR.</summary>
    public OcrDiagnostic? LastDiagnostic { get; private set; }

    /// <summary>
    /// Spustí snímkování (záznam o kontraktu v logu nebo ruční „Přečíst mobiGlas“).
    /// Když už běží, jen prodlouží sledování – log píše k jednomu kontraktu několik řádků po sobě.
    /// </summary>
    /// <param name="force">true = ruční „Přečíst mobiGlas“: čte se, i když mají všechny kontrakty údaje kompletní.</param>
    public void Trigger(bool force = false)
    {
        if (!Enabled || !_ocr.IsAvailable) return;
        // Všechny aktivní kontrakty už mají odměnu, zadavatele, popis i cíle → není co číst.
        if (!force && !_tracker.AnyActiveNeedsOcr()) return;
        CancellationToken token;
        lock (_lock)
        {
            _force |= force;
            _deadline = DateTime.UtcNow + WatchDuration;
            if (_running) return;
            _running = true;
            _session?.Cancel();
            _session = new CancellationTokenSource();
            token = _session.Token;
        }
        _ = Task.Run(() => RunSessionAsync(token));
    }

    private DateTime Deadline { get { lock (_lock) return _deadline; } }
    private void Extend() { lock (_lock) _deadline = DateTime.UtcNow + WatchDuration; }

    public void Stop()
    {
        lock (_lock) { _session?.Cancel(); _running = false; }
        SetState(Enabled ? MobiGlasState.Idle : MobiGlasState.Disabled);
    }

    private async Task RunSessionAsync(CancellationToken token)
    {
        SetState(MobiGlasState.Watching);
        var blankCount = 0;
        var finished = false;
        try
        {
            // mobiGlas má otevírací animaci
            await Task.Delay(600, token);
            while (DateTime.UtcNow < Deadline && !token.IsCancellationRequested)
            {
                var frame = await _capture.CaptureAsync();
                if (frame is not null)
                {
                    blankCount = 0;
                    var snap = await ProcessFrameAsync(frame, allowSkip: true);
                    if (snap.IsContractsApp)
                        Extend(); // hráč je v Contracts – pokračujeme
                    if (snap.IsAcceptedTab) SetState(MobiGlasState.AcceptedRead);
                    else if (State == MobiGlasState.CaptureBlank) SetState(MobiGlasState.Watching);
                    // Vše potřebné přečteno → konec (šetří CPU). Ruční čtení běží celou dobu.
                    bool force;
                    lock (_lock) force = _force;
                    if (snap.IsAcceptedTab && !force && !_tracker.AnyActiveNeedsOcr()) break;
                }
                else if (_capture.LastWasBlank && ++blankCount >= 2)
                {
                    SetState(MobiGlasState.CaptureBlank);
                }
                await Task.Delay(Interval, token);
            }
            finished = true;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppPaths.LogError(ex); }
        finally
        {
            bool again;
            lock (_lock) { _running = false; _force = false; again = finished && !token.IsCancellationRequested && _deadline > DateTime.UtcNow; }
            if (!token.IsCancellationRequested) _capture.EndSession();
            if (again) Trigger(); // záznam z logu přišel přesně ve chvíli ukončování
        }

        if (!token.IsCancellationRequested && State != MobiGlasState.CaptureBlank && State != MobiGlasState.AcceptedRead)
            SetState(MobiGlasState.Idle);
    }

    /// <summary>Zpracuje snímek obrazovky, který uložila hra (funguje i v exkluzivním fullscreenu).</summary>
    public async Task ProcessScreenshotAsync(string path)
    {
        if (!Enabled || !_ocr.IsAvailable) return;
        var frame = await GameScreenshotWatcher.LoadAsync(path);
        if (frame is null) return;
        var snap = await ProcessFrameAsync(frame, allowSkip: false);
        SetState(snap.IsAcceptedTab ? MobiGlasState.AcceptedRead : MobiGlasState.ScreenshotIgnored);
    }

    private async Task<MobiGlasSnapshot> ProcessFrameAsync(CapturedFrame frame, bool allowSkip)
    {
        await _ocrGate.WaitAsync();
        try
        {
            // 1) Obraz se od minula nezměnil (jen kurzor / blikání) → OCR se nespouští, použije se poslední výsledek.
            var signature = frame.Signature();
            if (allowSkip && _lastSnapshot is { } last && CapturedFrame.SignatureDistance(signature, _lastSignature) < 1.5)
            {
                OcrSkipped++;
                if (last.IsAcceptedTab) _tracker.ApplyMobiGlas(last);
                return last;
            }

            // 2) Když víme, kde je aplikace Contracts, čteme jen tento výřez (menší obraz = rychlejší OCR
            //    a u širokých monitorů bez zmenšování → ostřejší drobné písmo).
            var crop = _crop is { } c && _cropFrameSize == (frame.Width, frame.Height) ? c : ((double, double, double, double)?)null;
            IReadOnlyList<OcrLine> lines;
            if (crop is { } r)
            {
                var part = await _ocr.RecognizeAsync(frame.Crop(r.Item1, r.Item2, r.Item3, r.Item4));
                lines = part.Select(l => l.FromCrop(r.Item1, r.Item2, r.Item3, r.Item4)).ToList();
            }
            else lines = await _ocr.RecognizeAsync(frame);
            OcrRuns++;

            var snap = _parser.Parse(lines, l => frame.Luminance(l.X, l.Y, l.Width, l.Height));
            if (snap.IsContractsApp)
            {
                _crop = _parser.PanelBounds(lines);
                _cropFrameSize = (frame.Width, frame.Height);
            }
            else if (crop is not null)
            {
                _crop = null; // mobiGlas se posunul / zavřel → příště celý snímek
            }
            _lastSignature = signature;
            _lastSnapshot = snap;
            if (snap.IsContractsApp || LastDiagnostic is null)
                LastDiagnostic = new OcrDiagnostic(frame, lines, snap, _ocr.ActiveLanguage, DateTime.Now);
            if (snap.IsAcceptedTab)
            {
                _tracker.ApplyMobiGlas(snap);
                LastEntryCount = snap.Entries.Count;
                LastSource = frame.Source;
            }
            return snap;
        }
        finally { _ocrGate.Release(); }
    }

    private void SetState(MobiGlasState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose() => Stop();
}

public sealed record OcrDiagnostic(CapturedFrame Frame, IReadOnlyList<OcrLine> Lines, MobiGlasSnapshot Snapshot, string? OcrLanguage, DateTime Time);
