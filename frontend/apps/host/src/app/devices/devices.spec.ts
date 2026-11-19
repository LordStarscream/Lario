import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiErrorHandler } from '../../api/error-handler';
import type { DeviceDto } from '../../generated/models/DeviceDto';
import { SyncEndpointsService } from '../../generated/services/SyncEndpointsService';
import { Devices } from './devices';

const device: DeviceDto = {
  id: 'device-1', name: 'Mobilgerät', token: '',
  createdAtUtc: '2026-11-19T12:00:00Z', lastUsedAtUtc: null, revokedAtUtc: null,
};

beforeEach(async () => {
  await TestBed.configureTestingModule({
    imports: [Devices],
    providers: [{ provide: ApiErrorHandler, useValue: {
      handle: (error: Error) => ({ code: 'unknown', message: error.message }),
    } }],
  }).compileComponents();
  vi.spyOn(SyncEndpointsService, 'syncListDevices').mockResolvedValue([device]);
  vi.spyOn(SyncEndpointsService, 'syncRevokeDevice').mockResolvedValue(undefined);
});

afterEach(() => vi.restoreAllMocks());

describe('Devices', () => {
  it('zeigt Geräte und verlangt eine Bestätigung vor dem Widerruf', async () => {
    const fixture = TestBed.createComponent(Devices);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Mobilgerät');
    fixture.componentInstance.requestRevoke(device);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('wirklich widerrufen');
    expect(SyncEndpointsService.syncRevokeDevice).not.toHaveBeenCalled();
    fixture.componentInstance.cancelRevoke();
    await fixture.componentInstance.confirmRevoke();
    expect(SyncEndpointsService.syncRevokeDevice).not.toHaveBeenCalled();
  });

  it('widerruft nur das bestätigte Gerät und zeigt den Serverstatus', async () => {
    const component = TestBed.createComponent(Devices).componentInstance;
    await component.reload();
    component.requestRevoke(device);
    vi.mocked(SyncEndpointsService.syncListDevices).mockResolvedValue([
      { ...device, revokedAtUtc: '2026-11-19T13:00:00Z' },
    ]);
    await component.confirmRevoke();
    expect(SyncEndpointsService.syncRevokeDevice).toHaveBeenCalledExactlyOnceWith(device.id);
    expect(component.devices()[0].revokedAtUtc).toBe('2026-11-19T13:00:00Z');
    expect(component.pending()).toBeNull();
    expect(component.success()).toContain('widerrufen');
    component.requestRevoke(component.devices()[0]);
    expect(component.pending()).toBeNull();
  });

  it('bewahrt bei fehlgeschlagenem Nachladen den bereits bestätigten Widerruf', async () => {
    const component = TestBed.createComponent(Devices).componentInstance;
    await component.reload();
    component.requestRevoke(device);
    vi.mocked(SyncEndpointsService.syncListDevices).mockRejectedValue(new Error('Host nicht erreichbar'));
    await component.confirmRevoke();
    expect(component.devices()[0].revokedAtUtc).not.toBeNull();
    expect(component.pending()).toBeNull();
    expect(component.success()).toContain('widerrufen');
    expect(component.error()?.message).toBe('Host nicht erreichbar');
  });

  it('lässt nach fehlgeschlagenem Widerruf eine Wiederholung zu', async () => {
    const component = TestBed.createComponent(Devices).componentInstance;
    await component.reload();
    component.requestRevoke(device);
    vi.mocked(SyncEndpointsService.syncRevokeDevice).mockRejectedValueOnce(new Error('Netzwerkfehler'));
    await component.confirmRevoke();
    expect(component.devices()[0].revokedAtUtc).toBeNull();
    expect(component.pending()?.id).toBe(device.id);
    expect(component.success()).toBeNull();
    expect(component.busy()).toBe(false);
    await component.confirmRevoke();
    expect(SyncEndpointsService.syncRevokeDevice).toHaveBeenCalledTimes(2);
  });

  it('sperrt parallele Widerrufe während der Anfrage', async () => {
    const component = TestBed.createComponent(Devices).componentInstance;
    await component.reload();
    component.requestRevoke(device);
    let finish!: () => void;
    vi.mocked(SyncEndpointsService.syncRevokeDevice).mockImplementationOnce(
      () => new Promise<void>((resolve) => { finish = resolve; }) as never,
    );
    const first = component.confirmRevoke();
    await component.confirmRevoke();
    component.cancelRevoke();
    expect(component.pending()?.id).toBe(device.id);
    expect(SyncEndpointsService.syncRevokeDevice).toHaveBeenCalledTimes(1);
    finish();
    await first;
    expect(component.busy()).toBe(false);
  });
});
