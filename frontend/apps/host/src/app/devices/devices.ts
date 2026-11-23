import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import type { DeviceDto } from '../../generated/models/DeviceDto';
import { SyncEndpointsService } from '../../generated/services/SyncEndpointsService';

@Component({
  selector: 'app-devices',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  templateUrl: './devices.html',
})
export class Devices implements OnInit {
  private readonly errors = inject(ApiErrorHandler);
  readonly devices = signal<DeviceDto[]>([]);
  readonly busy = signal(false);
  readonly error = signal<ApiErrorInfo | null>(null);
  readonly success = signal<string | null>(null);
  readonly pending = signal<DeviceDto | null>(null);

  ngOnInit(): void {
    void this.reload();
  }

  async reload(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.devices.set(await SyncEndpointsService.syncListDevices());
    } catch (error) {
      this.error.set(this.errors.handle(error));
    } finally {
      this.busy.set(false);
    }
  }

  requestRevoke(device: DeviceDto): void {
    if (this.busy() || device.revokedAtUtc !== null) return;
    this.success.set(null);
    this.pending.set(device);
  }

  cancelRevoke(): void {
    if (!this.busy()) this.pending.set(null);
  }

  async confirmRevoke(): Promise<void> {
    const device = this.pending();
    if (this.busy() || device === null) return;
    this.busy.set(true);
    this.error.set(null);
    this.success.set(null);
    try {
      await SyncEndpointsService.syncRevokeDevice(device.id);
      // Der bestätigte Widerruf bleibt auch bei fehlgeschlagenem Nachladen sichtbar.
      this.devices.update((devices) => devices.map((item) => item.id === device.id
        ? { ...item, revokedAtUtc: new Date().toISOString() } : item));
      this.pending.set(null);
      this.success.set(`Zugang für „${device.name}“ widerrufen.`);
      this.devices.set(await SyncEndpointsService.syncListDevices());
    } catch (error) {
      this.error.set(this.errors.handle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
