import { computed, Injectable, signal } from '@angular/core';
import { MasterDataEndpointsService } from '../generated/services/MasterDataEndpointsService';
import type { AccountDto } from '../generated/models/AccountDto';
import type { CategoryDto } from '../generated/models/CategoryDto';
import type { PoolDto } from '../generated/models/PoolDto';
import type { TaxCategoryDto } from '../generated/models/TaxCategoryDto';

/**
 * Lädt die Stammdaten (Konten, Pools, Kategorien, Steuerkategorien)
 * einmalig für die Formularfelder. `all*` enthält auch archivierte
 * Einträge (Stammdaten-Panels); `*` sind die aktiven Einträge für
 * Auswahlfelder. Nach Schreiboperationen: {@link refresh}.
 */
@Injectable({ providedIn: 'root' })
export class CatalogService {
  readonly allAccounts = signal<AccountDto[]>([]);
  readonly allPools = signal<PoolDto[]>([]);
  readonly allCategories = signal<CategoryDto[]>([]);
  readonly allTaxCategories = signal<TaxCategoryDto[]>([]);
  readonly loading = signal(false);

  readonly accounts = computed(() =>
    this.allAccounts().filter((a) => a.status === 'active'),
  );
  readonly pools = computed(() =>
    this.allPools().filter((p) => p.status === 'active'),
  );
  readonly categories = computed(() =>
    this.allCategories().filter((c) => c.status === 'active'),
  );
  readonly taxCategories = computed(() =>
    this.allTaxCategories().filter((t) => t.status === 'active'),
  );

  private loaded = false;

  async load(): Promise<void> {
    if (this.loaded || this.loading()) {
      return;
    }
    this.loading.set(true);
    try {
      const [accounts, pools, categories, taxCategories] =
        await Promise.all([
          MasterDataEndpointsService.listAccounts(),
          MasterDataEndpointsService.listPools(),
          MasterDataEndpointsService.listCategories(),
          MasterDataEndpointsService.listTaxCategories(),
        ]);
      this.allAccounts.set(accounts);
      this.allPools.set(pools);
      this.allCategories.set(categories);
      this.allTaxCategories.set(taxCategories);
      this.loaded = true;
    } finally {
      this.loading.set(false);
    }
  }

  /** Zwangsfrisch nach Stammdaten-Änderungen. */
  async refresh(): Promise<void> {
    this.loaded = false;
    this.loading.set(false);
    await this.load();
  }
}
