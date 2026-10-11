import { TestBed } from '@angular/core/testing';
import { App } from './app';

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
      expect(app.saveError).toContain('Android-Paket');
      expect(app.entries).toHaveLength(0);
    });
  });

  describe('Eingabe-Validierung', () => {
    it('lehnt eine leere Bezeichnung ab', async () => {
      const { app } = createApp();
      app.label = '   ';
      await app.save();
      expect(app.saveError).toContain('Bezeichnung');
    });

    it('lehnt einen ungültigen Betrag ab', async () => {
      const { app } = createApp();
      app.label = 'Eintrag';
      app.amountInput = '12,345';
      await app.save();
      expect(app.saveError).toContain('Betrag');
    });

    it('übersetzt gültige Beträge in Cent (nur ganzzahlig)', () => {
      const { app } = createApp();
      const parse = (value: string): number | null | undefined => {
        app.amountInput = value;
        return (app as unknown as { parseAmount(): number | null | undefined }).parseAmount();
      };
      expect(parse('12,50')).toBe(1250);
      expect(parse('12.5')).toBe(1250);
      expect(parse('0')).toBe(0);
      expect(parse('')).toBeNull();
      expect(parse('12,345')).toBeUndefined();
      expect(parse('abc')).toBeUndefined();
    });

    it('formatiert Cent-Werte für die Anzeige ohne Float', () => {
      const { app } = createApp();
      expect(app.formatCents(1250)).toBe('12,50 €');
      expect(app.formatCents(5)).toBe('0,05 €');
      expect(app.formatCents(0)).toBe('0,00 €');
      expect(app.formatCents(null)).toBe('');
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
      app.hostUrl = 'http://192.168.1.226:8080';
      await app.testHost();
      expect(app.hostCheck?.ok).toBe(true);
      expect(app.hostCheck?.message).toContain('"status":"ok"');
      vi.unstubAllGlobals();
    });

    it('zeigt einen Netzwerkfehler (Host ausgefallen) als nicht erreichbar', async () => {
      vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
      const { app } = createApp();
      app.hostUrl = 'http://192.168.1.99:8080';
      await app.testHost();
      expect(app.hostCheck?.ok).toBe(false);
      expect(app.hostCheck?.message).toContain('Nicht erreichbar');
      vi.unstubAllGlobals();
    });

    it('zeigt einen HTTP-Fehlercode bei ungültigem Host-Antwortstatus', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(fakeResponse(503, 'boom')));
      const { app } = createApp();
      await app.testHost();
      expect(app.hostCheck?.ok).toBe(false);
      expect(app.hostCheck?.message).toContain('503');
      vi.unstubAllGlobals();
    });
  });
});
