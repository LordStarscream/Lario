namespace Lario.Domain;

/// <summary>
/// Basis für fachliche Fehler der Domain: ungültiger Zustand, verletzte
/// Invariante oder abgelehnte Eingabe. Keiner dieser Fehler darf zu einer
/// halben Übernahme führen — der Aufrufer behält den vorherigen Stand.
/// </summary>
public class LarioDomainException(string message) : Exception(message);

/// <summary>
/// Referenz auf eine nicht existierende oder fachlich unzulässige Entität
/// (z. B. Verteilung auf unbekannten Pool, Voreinstellung auf fehlende
/// Steuerkategorie).
/// </summary>
public sealed class InvalidReferenceException(string message)
    : LarioDomainException(message);

/// <summary>
/// Kategorie- oder Steuerkategorie-Beziehung würde einen Zyklus erzeugen
/// (Elternteil ist Nachkomme des neuen Kindes).
/// </summary>
public sealed class CycleDetectedException(string message)
    : LarioDomainException(message);

/// <summary>
/// Fachlicher Konflikt: dieselbe Identität wird erneut angelegt oder
/// geändert, der Inhalt ist aber ein anderer (z. B. gleicher
/// Anfangsbestands-ID mit anderem Betrag). Sichtbarer Fehler — kein
/// Last-Write-Wins (Rechenvertrag §5.5).
/// </summary>
public sealed class DomainConflictException(string message)
    : LarioDomainException(message);
