using Xunit;

// Die Api-Integrationstests starten je Testklasse eine eigene
// WebApplicationFactory (Kestrel + SQLite). Unter xUnit-Parallelität
// konkurrierten die Klassen intermittierend (flakige Fehler bei
// vollem Lauf, isoliert immer grün). Serialisierung macht den Lauf
// deterministisch — der Mehraufwand ist Sekundenbruchteile.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
