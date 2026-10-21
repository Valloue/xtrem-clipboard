using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Enveloppe un formateur et retire le segment utilisateur des chemins
/// (<c>X:\Users\nom</c>), y compris dans les stack traces et le JSON échappé.
/// Les secrets ne se filtrent pas ici : on ne les passe pas au logger.
/// </summary>
public sealed class ScrubbingTextFormatter : ITextFormatter
{
    // Une ou deux antislashs : texte brut, ou JSON déjà échappé (C:\\Users\\nom).
    private static readonly Regex UserPath = new(
        @"[A-Za-z]:(?:\\\\|\\)users(?:\\\\|\\)[^\\""\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ITextFormatter _inner;

    public ScrubbingTextFormatter(ITextFormatter inner)
    {
        _inner = inner;
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(UserPath.Replace(buffer.ToString(), "%USERPROFILE%"));
    }
}
