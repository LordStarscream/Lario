namespace Lario.Domain.MasterData;

/// <summary>
/// Steuer-Voreinstellung einer Alltagskategorie (Rechenvertrag §3.1,
/// Plan §9 „Kennzeichnung und Voreinstellungen").
///
/// Semantik (verbindlich, Schritt 3):
/// - <see cref="TaxRelevant"/>: steuerlich relevant ja/nein; null bedeutet
///   „nicht festgelegt" (Keine Markierung).
/// - <see cref="TaxCategoryId"/>: optional gewählte Steuerkategorie (eigener
///   Baum, siehe <see cref="TaxCategory"/>); muss zur Anlage existieren.
/// - <see cref="InheritToChildren"/>: die Voreinstellung gilt auch für
///   Untergruppen, die selbst keine explizite Voreinstellung haben.
///
/// Vererbung: Die wirksame Voreinstellung einer Kategorie ist die Voreinstellung
/// der nächsten Vorfahrin (einschließlich der Kategorie selbst), die eine
/// explizite Voreinstellung trägt. Eine explizite Voreinstellung blockiert
/// die Vererbung aus höheren Ebenen — insbesondere ersetzt ein explizites
/// Nein (TaxRelevant = false) keine übergeordnete Vererbung und wird nie
/// durch eine übergeordnete Vererbung ersetzt.
/// </summary>
public sealed class TaxPreset
{
    /// <summary>Kategorie, zu der die Voreinstellung gehört (1:1).</summary>
    public string CategoryId { get; init; } = string.Empty;

    /// <summary>Ja/nein; null = nicht festgelegt (Keine Markierung).</summary>
    public bool? TaxRelevant { get; set; }

    /// <summary>Optional gewählte Steuerkategorie; null = keine gewählt.</summary>
    public string? TaxCategoryId { get; set; }

    /// <summary>Vererbung auf Untergruppen ohne eigene Voreinstellung.</summary>
    public bool InheritToChildren { get; set; }

    /// <summary>Eigene Basisrevision; Startwert 1 (Rechenvertrag §5.5).</summary>
    public int BaseRevision { get; set; } = 1;
}
