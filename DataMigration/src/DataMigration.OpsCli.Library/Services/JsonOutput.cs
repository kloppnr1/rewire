using System.Text.Encodings.Web;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Hvordan vi skriver JSON ud af huset — til en fil eller en rapportcelle et menneske åbner.
/// </summary>
public static class JsonOutput
{
    /// <summary>
    /// System.Text.Json undslipper som standard alt uden for ASCII, så æ, ø og å bliver til
    /// escape-sekvenser. Det er gyldig JSON, men filerne her bliver åbnet af mennesker, og danske
    /// navne fylder det meste af indholdet.
    ///
    /// <para>Den afslappede encoder er sikker de steder den bruges: indholdet skrives til disk
    /// eller til en rapportcelle. Den må <b>ikke</b> bruges på JSON der lægges ind i HTML — der
    /// er escapingen af tegn som mindre-end og ampersand netop pointen — og den hører heller ikke
    /// hjemme på det vi sender til Settl, hvor formatet er en aftale med modparten og ikke et
    /// spørgsmål om læsbarhed.</para>
    /// </summary>
    public static JavaScriptEncoder HumanReadable => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
}
