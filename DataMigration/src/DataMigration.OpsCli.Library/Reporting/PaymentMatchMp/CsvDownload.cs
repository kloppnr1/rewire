using System.Text;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>
/// Det ene sted hvor en færdig CSV-tekst bliver til de bytes vi sender retur.
///
/// <para>Dansk Excel læser en CSV uden byte order mark som ANSI, og så bliver "Målepunkt" til
/// "MÃ¥lepunkt". Filer et menneske åbner skal derfor have et BOM foran — <see cref="ForExcel"/>.
/// Filer en maskine læser (et opslag, en forespørgsel) skal ikke: det usynlige tegn foran første
/// felt er dét der får modtageren til at svare "ingen rækker" — <see cref="ForMachine"/>.</para>
///
/// <para>Fælden der gjorde helperen nødvendig: <c>new UTF8Encoding(encoderShouldEmitUTF8Identifier:
/// true).GetBytes(tekst)</c> skriver <em>ikke</em> et BOM. Flaget gælder kun
/// <see cref="Encoding.GetPreamble"/> og dermed StreamWriter — <c>GetBytes</c> giver præcis de
/// samme bytes som uden flaget. Derfor sætter <see cref="ForExcel"/> preamblen på selv.</para>
/// </summary>
public static class CsvDownload
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 med BOM. Til filer et menneske åbner i Excel.</summary>
    public static byte[] ForExcel(string csv)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Utf8NoBom.GetBytes(csv);
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    /// <summary>UTF-8 uden BOM. Til filer der bliver læst af noget andet end Excel.</summary>
    public static byte[] ForMachine(string csv) => Utf8NoBom.GetBytes(csv);
}
