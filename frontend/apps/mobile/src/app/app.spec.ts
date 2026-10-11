import { TestBed } from '@angular/core/testing';
import { App, parseAmount } from './app';
import { PrototypeEntry, SqliteService } from './sqlite.service';

/** Minimale Fetch-Antwort ohne jsdom-/Node-`Response`-Abhängigkeit. */
function fakeResponse(status: number, body: string): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => body,
  } as Response;
}

describe('App (Prototyp Schritt 2)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
    }).compileComponents();
  });

  function createApp() {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    fixture.detectChanges();
    return { fixture, app };
  }

  it('should create the app', () => {
    const { app } = createApp();
    expect(app).toBeTruthy();
  });

  it('should render the Lario heading', async () => {
    const { fixture } = createApp();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Lario');
  });

  describe('im Browser (ohne Native-Plugin)', () => {
    it('kennt die Plattform als nicht-nativ und zeigt den Browser-Hinweis', async () => {
      const { fixture, app } = createApp();
      await fixture.whenStable();
      expect(app.isNative).toBe(false);
      const compiled = fixture.nativeElement as HTMLElement;
      expect(compiled.querySelector('.warning')?.textContent).toContain('Android-Paket');
    });

    it('verweigert das Speichern mit klarer Meldung statt einem Absturz', async () => {
      const { app } = createApp();
      app.label = 'Prototyp-Eintrag';
      await app.save();
      expect(app.saveError()).toContain('Android-App');
      expect(app.entries()).toHaveLength(0);
    });
  });

  describe('Eingabe-Validierung', () => {
    it('lehnt eine leere Bezeichnung ab', async () => {
      const { app } = createApp();
      app.label = '   ';
      await app.save();
      expect(app.saveError()).toContain('Bezeichnung');
    });

    it('lehnt einen ungültigen Betrag ab', async () => {
      const { app } = createApp();
      app.label = 'Eintrag';
      app.amountInput = '12,345';
      await app.save();
      expect(app.saveError()).toContain('Betrag');
    });

    it('übersetzt gültige Beträge in Cent (nur ganzzahlig)', () => {
      expect(parseAmount('12,50')).toBe(1250);
      expect(parseAmount('12.5')).toBe(1250);
      expect(parseAmount('0')).toBe(0);
      expect(parseAmount('')).toBeNull();
      expect(parseAmount('12,345')).toBeUndefined();
      expect(parseAmount('abc')).toBeUndefined();
      // Grenzfall: 13 Stellen vor dem Komma sind die Systemgrenze
      // (long-Cent-Speicher); 10^15 Cent ist noch ein sicheres
      // TypeScript-Ganzzahl, 14 Stellen werden abgelehnt.
      expect(parseAmount('9999999999999,99')).toBe(999999999999999);
      expect(parseAmount('10000000000000,00')).toBeUndefined();
    });

    it('formatiert Cent-Werte für die Anzeige ohne Float', () => {
      const { app } = createApp();
      expect(app.formatCents(1250)).toBe('12,50 €');
      expect(app.formatCents(5)).toBe('0,05 €');
      expect(app.formatCents(0)).toBe('0,00 €');
    });
  });

  describe('Host-Verbindungstest', () => {
    it('zeigt einen erfolgreichen Health-Abruf', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockResolvedValue(
          fakeResponse(200, JSON.stringify({ status: 'ok', dataDirectory: '/tmp/x' })),
        ),
      );
      const { app } = createApp();
      app.hostUrl.set('http://192.168.1.226:8080');
      await app.testHost();
      expect(app.hostCheck()?.ok).toBe(true);
      expect(app.hostCheck()?.message).toContain('"status":"ok"');
      vi.unstubAllGlobals();
    });

    it('zeigt einen Netzwerkfehler (Host ausgefallen) als nicht erreichbar', async () => {
      vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
      const { app } = createApp();
      app.hostUrl.set('http://192.168.1.99:8080');
      await app.testHost();
      expect(app.hostCheck()?.ok).toBe(false);
      expect(app.hostCheck()?.message).toContain('Nicht erreichbar');
      vi.unstubAllGlobals();
    });

    it('zeigt einen HTTP-Fehlercode bei ungültigem Host-Antwortstatus', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(fakeResponse(503, 'boom')));
      const { app } = createApp();
      await app.testHost();
      expect(app.hostCheck()?.ok).toBe(false);
      expect(app.hostCheck()?.message).toContain('503');
      vi.unstubAllGlobals();
    });
  });
});

/**
 * Zoneless-Prüfung (Gerätebefund OnePlus 9): Angular läuft in diesem
 * Projekt zoneless (Angular 22, kein zone.js), und nur Signal-Updates
 * melden asynchrone Änderungen nach `await` der Change-Detection. Die
 * Mocks lösen verzögert auf, damit die Ergebnisse erst NACH dem
 * initialen CD-Pass eintreffen; die Assertions verwenden danach KEIN
 * manuelles detectChanges() — der DOM-Update muss von Angular selbst
 * kommen (ohne diesen Fix waren die Werte erst nach Tippen in ein
 * Eingabefeld sichtbar).
 */
describe('App (zoneless: asynchrone Zustände ohne weitere Interaktion sichtbar)', () => {
  const entry: PrototypeEntry = {
    id: '11111111-2222-4333-8444-555555555555',
    label: 'Geräte-Test',
    amountCents: 1250,
    createdAtUtc: '2026-10-11T12:00:00.000Z',
  };

  function delay(value: unknown, ms = 25): Promise<unknown> {
    return new Promise((resolve) => setTimeout(() => resolve(value), ms));
  }

  const sqliteMock = {
    isNative: true,
    ready: true,
    init: () => delay(undefined),
    getDeviceId: () => delay('device-id-0001'),
    listEntries: () => delay([entry]),
    getMeta: () => delay(null),
    setMeta: () => delay(undefined),
    saveEntry: (label: string, amountCents: number | null) =>
      delay({
        ...entry,
        id: '99999999-8888-4777-8666-555555555555',
        label,
        amountCents,
      }),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [{ provide: SqliteService, useValue: sqliteMock }],
    }).compileComponents();
  });

  function createApp() {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    // Der initiale CD-Pass löst ngOnInit aus; danach kein manuelles
    // detectChanges() mehr (das wäre die Abkürzung, die den Bug
    // verbergen würde).
    fixture.detectChanges();
    return { fixture, app };
  }

  /**
   * Wartet, bis Angular den von einem Signal-Update ausgelösten
   * Zoneless-CD-Pass selbst ausgeführt hat (DOM-Abfrage, kein
   * detectChanges). Hinweis: Im Zoneless-Testumgebung (kein zone.js)
   * trackt `fixture.whenStable()` nur Angular-interne Tasks, nicht die
   * rohen setTimeouts der verzögerten Mocks — daher Polling auf den
   * DOM-Zustand.
   */
  async function untilVisible(check: () => boolean, description: string): Promise<void> {
    const deadline = Date.now() + 2000;
    while (Date.now() < deadline) {
      if (check()) {
        return;
      }
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    throw new Error(
      `Zeitüberschreitung: „${description}“ wurde im DOM nicht sichtbar ` +
        '(Zoneless-CD hat das asynchrone Update nicht gemeldet?)',
    );
  }

  it('zeigt Geräte-ID und gespeicherte Einträge nach Appstart ohne Nutzerinteraktion', async () => {
    const { fixture } = createApp();
    const el = fixture.nativeElement as HTMLElement;
    // Geräte-ID und Eintragsliste treffen zu verschiedenen Zeitpunkten
    // ein (separate verzögerte Mocks) — auf den kompletten Endzustand
    // warten, nicht nur auf den ersten Signal-Update.
    await untilVisible(
      () =>
        (el.querySelector('.device code')?.textContent?.includes('device-id-0001') ?? false) &&
        (el.querySelector('.count')?.textContent?.includes('1 Einträge') ?? false),
      'Appstart-Zustand (Geräte-ID + gespeicherter Eintrag)',
    );
    expect(el.querySelector('.count')?.textContent).toContain('1 Einträge');
    expect(el.querySelector('.entry-label')?.textContent).toContain('Geräte-Test');
    expect(el.querySelector('.entry-amount')?.textContent).toBe('12,50 €');
  });

  it('zeigt das Speichern-Ergebnis ohne weitere Interaktion', async () => {
    const { fixture, app } = createApp();
    const el = fixture.nativeElement as HTMLElement;
    // Appstart-Zustand abwarten (Signal-CD muss ihn gerendert haben).
    await untilVisible(
      () => el.querySelector('.count')?.textContent?.includes('1 Einträge') ?? false,
      'Appstart-Eintrag',
    );
    app.label = 'Neuer Eintrag';
    app.save();
    await untilVisible(
      () => (el.textContent ?? '').includes('Neuer Eintrag'),
      'neu gespeicherter Eintrag',
    );
    expect(el.querySelector('.count')?.textContent).toContain('2 Einträge');
    // Busy-Zustand ist gemeldet: Der Speicher-Button ist wieder aktiv.
    const saveButton = Array.from(el.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Lokal speichern'),
    );
    expect(saveButton?.disabled).toBe(false);
  });

  it('zeigt das Host-Testergebnis ohne weitere Interaktion', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        () =>
          new Promise<Response>((resolve) =>
            setTimeout(
              () => resolve(fakeResponse(200, JSON.stringify({ status: 'ok' }))),
              25,
            ),
          ),
      ),
    );
    const { fixture } = createApp();
    const el = fixture.nativeElement as HTMLElement;
    (fixture.componentInstance as App).testHost();
    await untilVisible(
      () => el.querySelector('.ok')?.textContent?.includes('Erreichbar') ?? false,
      'Host-Testergebnis',
    );
    expect(el.querySelector('.ok')?.textContent).toContain('Erreichbar');
    vi.unstubAllGlobals();
  });
});
