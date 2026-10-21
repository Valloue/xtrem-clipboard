namespace XtremClipboard.Services;

/// <summary>Décision de l'ordonnanceur de rafale presse-papiers.</summary>
public enum CaptureSchedule
{
    /// <summary>Rien à faire.</summary>
    Idle = 0,

    /// <summary>Attendre la fin de la fenêtre de silence.</summary>
    Wait = 1,

    /// <summary>Lire le presse-papiers maintenant.</summary>
    Capture = 2,

    /// <summary>Relecture refusée : le numéro de séquence n'a pas changé.</summary>
    SkipSameSequence = 3
}

/// <summary>Résultat pur, sans horloge réelle : action + délai de silence restant (≤ 150 ms).</summary>
public readonly record struct ScheduleResult(CaptureSchedule Schedule, int WaitMs)
{
    public static ScheduleResult Idle { get; } = new(CaptureSchedule.Idle, 0);

    public static ScheduleResult Capture { get; } = new(CaptureSchedule.Capture, 0);

    public static ScheduleResult SkipSameSequence { get; } = new(CaptureSchedule.SkipSameSequence, 0);

    public static ScheduleResult Wait(int waitMs) =>
        new(CaptureSchedule.Wait, Math.Clamp(waitMs, 1, ClipboardCaptureScheduler.SilenceWindowMs));
}

/// <summary>
/// Une rafale de notifications = une lecture, en fin de fenêtre de silence.
/// Une notification pendant la lecture est retenue : on ne relance que si le numéro
/// de séquence a changé. Horloge injectée (<c>nowMs</c>) — pas de <c>Task.Delay</c>.
/// </summary>
public sealed class ClipboardCaptureScheduler
{
    /// <summary>Fenêtre de silence maximale. Au-delà, deux copies volontaires ne sont plus fusionnées.</summary>
    public const int SilenceWindowMs = 150;

    private bool _capturing;
    private bool _armed;
    private long _dueAtMs;
    private bool _held;
    private uint _heldSequence;
    private bool _hasRead;
    private uint _lastReadSequence;

    /// <summary>
    /// Notification presse-papiers. Arme une seule lecture en fin de silence ;
    /// une notification suivante annule et réarme. Pendant une lecture, la notification est retenue.
    /// </summary>
    public ScheduleResult OnNotification(uint sequence, long nowMs)
    {
        if (_capturing)
        {
            _held = true;
            _heldSequence = sequence;
            return ScheduleResult.Idle;
        }

        if (_hasRead && sequence == _lastReadSequence)
            return ScheduleResult.SkipSameSequence;

        Arm(nowMs);
        return ScheduleResult.Wait(SilenceWindowMs);
    }

    /// <summary>
    /// Rattrapage explicite (fin d'init). Pas de fenêtre de silence : le contenu déjà
    /// posé doit être lu. Pendant une lecture, même règle de rétention que une notification.
    /// </summary>
    public ScheduleResult OnCatchUp(uint sequence, long nowMs)
    {
        if (_capturing)
        {
            _held = true;
            _heldSequence = sequence;
            return ScheduleResult.Idle;
        }

        if (_hasRead && sequence == _lastReadSequence)
            return ScheduleResult.SkipSameSequence;

        _armed = false;
        _ = nowMs;
        return ScheduleResult.Capture;
    }

    /// <summary>L'horloge fournie a avancé. <see cref="CaptureSchedule.Capture"/> seulement si le silence est écoulé.</summary>
    public ScheduleResult OnClock(long nowMs)
    {
        if (_capturing || !_armed)
            return ScheduleResult.Idle;

        if (nowMs < _dueAtMs)
        {
            var remaining = (int)Math.Min(SilenceWindowMs, _dueAtMs - nowMs);
            return ScheduleResult.Wait(Math.Max(1, remaining));
        }

        _armed = false;
        return ScheduleResult.Capture;
    }

    /// <summary>Démarre la lecture. Les notifications déjà fusionnées dans le silence ne sont pas « retenues ».</summary>
    public void BeginCapture()
    {
        _capturing = true;
        _armed = false;
        _held = false;
    }

    /// <summary>Abandonne une notification retenue (pause, exclusion) pour ne pas relancer en boucle.</summary>
    public void DropHeld() => _held = false;

    /// <summary>
    /// Fin de lecture. <paramref name="read"/> indique qu'un numéro a été observé.
    /// <paramref name="suppressed"/> : la lecture n'a pas pu se faire (écriture de l'app) — on réarme.
    /// </summary>
    public ScheduleResult EndCapture(bool read, uint readSequence, long nowMs, bool suppressed)
    {
        _capturing = false;

        if (read && !suppressed)
        {
            _hasRead = true;
            _lastReadSequence = readSequence;
        }

        if (suppressed)
        {
            _held = false;
            Arm(nowMs);
            return ScheduleResult.Wait(SilenceWindowMs);
        }

        if (!_held)
            return ScheduleResult.Idle;

        var held = _heldSequence;
        _held = false;

        if (read && held == readSequence)
            return ScheduleResult.SkipSameSequence;

        Arm(nowMs);
        return ScheduleResult.Wait(SilenceWindowMs);
    }

    private void Arm(long nowMs)
    {
        _armed = true;
        _dueAtMs = nowMs + SilenceWindowMs;
    }
}
