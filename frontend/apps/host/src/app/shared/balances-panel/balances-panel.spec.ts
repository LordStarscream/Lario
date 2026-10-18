import { Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { BalancesPanel } from './balances-panel';
import type { BalancesDto } from '../../../generated/models/BalancesDto';

/**
 * Gemeinsames Salden-Panel (Zwischenstep 7.5, Review R7-8/R7-9):
 * reine Präsentations-Komponente über dem abgeleiteten
 * GET /api/v1/balances — rendert die serverseitig gelieferten Werte
 * (keine clientseitige Saldo-Rechnung) und den unzugeordneten
 * Anfangsbestand-Rest als eigene Sektion.
 */

function balancesDto(overrides: Partial<BalancesDto> = {}): BalancesDto {
  return {
    accounts: [
      { accountId: 'acc-1', name: 'Giro', status: 'active', balance: 1200 },
      { accountId: 'acc-2', name: 'Sparkasse', status: 'inactive', balance: 300 },
    ],
    pools: [{ poolId: 'pool-1', name: 'Flex', status: 'active', balance: 500 }],
    totalAccountBalance: 1500,
    totalPoolBalance: 500,
    totalIncome: 0,
    accountPoolGap: 1000,
    unallocatedOpeningBalances: [
      {
        openingBalanceId: 'ob-1',
        date: '2026-01-01',
        accountId: 'acc-1',
        accountName: 'Giro',
        unallocated: 700,
      },
      {
        openingBalanceId: 'ob-2',
        date: '2026-02-01',
        accountId: 'acc-2',
        accountName: null,
        unallocated: 300,
      },
    ],
    ...overrides,
  };
}

/**
 * Testharness: überträgt das `required`-Input (in Tests nicht direkt
 * setzbar) per Template-Bindung.
 */
@Component({
  template: '<app-balances-panel [balances]="dto()" />',
  imports: [BalancesPanel],
})
class Harness {
  readonly dto = signal<BalancesDto>(balancesDto());
}

function createFixture(dto: BalancesDto = balancesDto()): ComponentFixture<Harness> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ imports: [Harness] });
  const fixture = TestBed.createComponent(Harness);
  fixture.componentInstance.dto.set(dto);
  fixture.detectChanges();
  return fixture;
}

/**
 * Textinhalt mit normalisierten Leerzeichen: `Intl.NumberFormat` setzt
 * zwischen Betrag und Währung einen No-Break-Space (U+00A0/U+202F).
 */
function text(el: Element | HTMLElement | null): string {
  return (el?.textContent ?? '').replace(/[\u00A0\u202F]/g, ' ');
}

describe('BalancesPanel — Salden (Zwischenstep 7.5)', () => {
  it('rendert Konten- und Pool-Salden mit Summenzeilen', () => {
    const fixture = createFixture();
    const container = fixture.nativeElement as HTMLElement;

    // Kopf: serverseitige Summen + Differenz (Kontostände − Poolsummen).
    const toggle = container.querySelector('.balances-toggle')!;
    expect(text(toggle)).toContain('Konten: 15,00 €');
    expect(text(toggle)).toContain('Pools: 5,00 €');
    expect(text(toggle)).toContain('Differenz: 10,00 €');

    // Konten-Tabelle: Salden + inaktives Konto mit Status-Badge.
    expect(text(container)).toContain('Giro');
    expect(text(container)).toContain('12,00 €');
    expect(text(container)).toContain('Sparkasse');
    expect(text(container)).toContain('inactive');

    // Pool-Tabelle.
    expect(text(container)).toContain('Flex');
    expect(text(container)).toContain('5,00 €');

    // Summenzeilen des Salden-Grids (Konten 15,00 €, Pools 5,00 €);
    // die Summenzeile der Unallocated-Sektion zählt nicht dazu.
    const totalRows =
      container.querySelectorAll('.balances-grid .balances-total-row td');
    expect(totalRows).toHaveLength(4);
    expect(text(totalRows[1])).toContain('15,00 €');
    expect(text(totalRows[3])).toContain('5,00 €');
  });

  it('zeigt unzugeordnete Anfangsbestände mit Summe (Review R7-9)', () => {
    const fixture = createFixture();
    const container = fixture.nativeElement as HTMLElement;

    const section = container.querySelector('.balances-unallocated')!;
    expect(text(section)).toContain('Unzugeordnete Anfangsbestände');
    // Pro Eintrag: Stichtag (de-DE), Konto, Betrag.
    expect(text(section)).toContain('01.01.2026');
    expect(text(section)).toContain('Giro');
    expect(text(section)).toContain('7,00 €');
    expect(text(section)).toContain('01.02.2026');
    // Fehlender Kontoname fällt auf die Id zurück.
    expect(text(section)).toContain('acc-2');
    expect(text(section)).toContain('3,00 €');

    // Summenzeile = Summe der serverseitig gelieferten Restbeträge (1.000 €).
    const total = section.querySelectorAll('.balances-total-row td');
    expect(total).toHaveLength(2);
    expect(text(total[1])).toContain('10,00 €');
  });

  it('zeigt bei vollständig verteilteren Anfangsbeständen den Hinweis (Review R7-9)', () => {
    const fixture = createFixture(balancesDto({ unallocatedOpeningBalances: [] }));
    const section = (fixture.nativeElement as HTMLElement).querySelector(
      '.balances-unallocated',
    )!;
    expect(text(section)).toContain(
      'Keine — alle Anfangsbestände sind vollständig auf Pools verteilt.',
    );
  });

  it('kann das Panel einklappen und wieder aufklappen', () => {
    const fixture = createFixture();
    const container = fixture.nativeElement as HTMLElement;
    const toggle = container.querySelector(
      '.balances-toggle',
    ) as HTMLButtonElement;

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(container.querySelector('.balances-grid')).not.toBeNull();

    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(container.querySelector('.balances-grid')).toBeNull();

    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(container.querySelector('.balances-grid')).not.toBeNull();
  });
});
