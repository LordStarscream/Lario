import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { MasterDataEndpointsService } from '../../generated/services/MasterDataEndpointsService';
import type { TaxPresetDto } from '../../generated/models/TaxPresetDto';

type EntityKind = 'account' | 'pool' | 'category' | 'taxCategory';

interface RenameRow {
  kind: EntityKind;
  id: string;
  currentName: string;
}

/**
 * Stammdaten minimal verwalten (Schritt 7, T6): Konten, Pools,
 * Kategorien und Steuerkategorien anlegen, umbenennen, archivieren
 * und reaktivieren; Steuer-Voreinstellungen auf Kategorien setzen.
 */
@Component({
  selector: 'app-master-data',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './master-data.html',
  styleUrl: './master-data.css',
})
export class MasterData implements OnInit {
  private readonly catalog = inject(CatalogService);
  private readonly errors = inject(ApiErrorHandler);

  readonly allAccounts = this.catalog.allAccounts;
  readonly allPools = this.catalog.allPools;
  readonly allCategories = this.catalog.allCategories;
  readonly allTaxCategories = this.catalog.allTaxCategories;
  readonly accounts = this.catalog.accounts;
  readonly categories = this.catalog.categories;
  readonly taxCategories = this.catalog.taxCategories;
  readonly loading = this.catalog.loading;

  readonly busy = signal(false);
  readonly error = signal<ApiErrorInfo | null>(null);
  readonly success = signal<string | null>(null);

  // Anlage-Formulare
  newAccountName = '';
  newAccountType = 'Bank';
  newAccountInterestBearing = false;
  newPoolName = '';
  newCategoryName = '';
  newCategoryParent = '';
  newTaxCategoryName = '';
  newTaxCategoryParent = '';

  // Umbenennen
  readonly rename = signal<RenameRow | null>(null);
  renameText = '';

  // Steuer-Voreinstellung
  readonly presetTarget = signal<{ id: string; name: string } | null>(null);
  presetRelevant = true;
  presetTaxCategoryId = '';
  presetInherit = true;

  ngOnInit(): void {
    void this.catalog.load();
  }

  async reload(): Promise<void> {
    this.success.set(null);
    await this.catalog.refresh();
  }

  private fail(error: unknown): void {
    this.error.set(this.errors.handle(error));
    this.busy.set(false);
  }

  private ok(message: string): void {
    this.error.set(null);
    this.success.set(message);
  }

  // ---------- Anlage ----------

  async createAccount(): Promise<void> {
    const name = this.newAccountName.trim();
    if (this.busy() || name === '') {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.createAccount({
        name,
        type: this.newAccountType,
        isInterestBearing: this.newAccountInterestBearing,
      });
      this.newAccountName = '';
      this.newAccountInterestBearing = false;
      this.ok(`Konto „${name}“ angelegt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  async createPool(): Promise<void> {
    const name = this.newPoolName.trim();
    if (this.busy() || name === '') {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.createPool({ name });
      this.newPoolName = '';
      this.ok(`Pool „${name}“ angelegt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  async createCategory(): Promise<void> {
    const name = this.newCategoryName.trim();
    if (this.busy() || name === '') {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.createCategory({
        name,
        parentId: this.newCategoryParent === '' ? null : this.newCategoryParent,
      });
      this.newCategoryName = '';
      this.newCategoryParent = '';
      this.ok(`Kategorie „${name}“ angelegt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  async createTaxCategory(): Promise<void> {
    const name = this.newTaxCategoryName.trim();
    if (this.busy() || name === '') {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.createTaxCategory({
        name,
        parentId:
          this.newTaxCategoryParent === '' ? null : this.newTaxCategoryParent,
      });
      this.newTaxCategoryName = '';
      this.newTaxCategoryParent = '';
      this.ok(`Steuerkategorie „${name}“ angelegt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  // ---------- Umbenennen ----------

  openRename(row: RenameRow): void {
    this.rename.set(row);
    this.renameText = row.currentName;
    this.error.set(null);
  }

  closeRename(): void {
    this.rename.set(null);
  }

  async submitRename(): Promise<void> {
    const row = this.rename();
    const name = this.renameText.trim();
    if (this.busy() || !row || name === '' || name === row.currentName) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const request = { name };
      switch (row.kind) {
        case 'account':
          await MasterDataEndpointsService.renameAccount(row.id, request);
          break;
        case 'pool':
          await MasterDataEndpointsService.renamePool(row.id, request);
          break;
        case 'category':
          await MasterDataEndpointsService.renameCategory(row.id, request);
          break;
        case 'taxCategory':
          await MasterDataEndpointsService.renameTaxCategory(row.id, request);
          break;
      }
      this.closeRename();
      this.ok(`„${row.currentName}“ in „${name}“ umbenannt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- Archivieren / Reaktivieren ----------

  private async setArchived(kind: EntityKind, id: string, name: string, archived: boolean): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      if (archived) {
        switch (kind) {
          case 'account':
            await MasterDataEndpointsService.archiveAccount(id);
            break;
          case 'pool':
            await MasterDataEndpointsService.archivePool(id);
            break;
          case 'category':
            await MasterDataEndpointsService.archiveCategory(id);
            break;
          case 'taxCategory':
            await MasterDataEndpointsService.archiveTaxCategory(id);
            break;
        }
      } else {
        switch (kind) {
          case 'account':
            await MasterDataEndpointsService.unarchiveAccount(id);
            break;
          case 'pool':
            await MasterDataEndpointsService.unarchivePool(id);
            break;
          case 'category':
            await MasterDataEndpointsService.unarchiveCategory(id);
            break;
          case 'taxCategory':
            await MasterDataEndpointsService.unarchiveTaxCategory(id);
            break;
        }
      }
      this.ok(`„${name}“ ${archived ? 'archiviert' : 'reaktiviert'}.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  archive(kind: EntityKind, id: string, name: string): void {
    void this.setArchived(kind, id, name, true);
  }

  unarchive(kind: EntityKind, id: string, name: string): void {
    void this.setArchived(kind, id, name, false);
  }

  // ---------- Steuer-Voreinstellung ----------

  openPreset(id: string, name: string): void {
    this.presetTarget.set({ id, name });
    this.presetRelevant = true;
    this.presetTaxCategoryId = '';
    this.presetInherit = true;
    this.error.set(null);
  }

  closePreset(): void {
    this.presetTarget.set(null);
  }

  async submitPreset(): Promise<void> {
    const target = this.presetTarget();
    if (this.busy() || !target) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.setTaxPreset(target.id, {
        taxRelevant: this.presetRelevant,
        taxCategoryId:
          this.presetRelevant && this.presetTaxCategoryId !== ''
            ? this.presetTaxCategoryId
            : null,
        inheritToChildren: this.presetInherit,
      });
      this.closePreset();
      this.ok(`Voreinstellung für „${target.name}“ gespeichert.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  async removePreset(): Promise<void> {
    const target = this.presetTarget();
    if (this.busy() || !target) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await MasterDataEndpointsService.removeTaxPreset(target.id);
      this.closePreset();
      this.ok(`Voreinstellung für „${target.name}“ entfernt.`);
      await this.reload();
    } catch (error) {
      this.fail(error);
    }
  }

  presetLabel(preset: TaxPresetDto | null): string {
    if (preset === null) {
      return '—';
    }
    if (preset.taxRelevant === true) {
      return `relevant${preset.taxCategoryName ? ` (${preset.taxCategoryName})` : ''}`;
    }
    if (preset.taxRelevant === false) {
      return 'nicht relevant';
    }
    return 'offen';
  }
}
