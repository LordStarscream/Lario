import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, vi } from 'vitest';
import { MasterData } from './master-data';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiRequestOptions } from '../../generated/core/ApiRequestOptions';
import type { ApiResult } from '../../generated/core/ApiResult';
import { CatalogService } from '../../api/catalogs';
import { MasterDataEndpointsService } from '../../generated/services/MasterDataEndpointsService';

/**
 * Review R4 F2: busy bleibt nach erfolgreichen Schreiboperationen
 * hängen (true) — die Seite ist ab da eingefroren. Die Fixes setzen
 * busy in allen Schreibmethoden über finally zurück.
 *
 * Ablauf-Tests: echte Komponentenklasse, nur die API-Ebene und die
 * Injections sind simuliert.
 */
const MASTERDATA_METHODS = [
  'createAccount',
  'createPool',
  'createCategory',
  'createTaxCategory',
  'renameAccount',
  'renamePool',
  'renameCategory',
  'renameTaxCategory',
  'archiveAccount',
  'archivePool',
  'archiveCategory',
  'archiveTaxCategory',
  'unarchiveAccount',
  'unarchivePool',
  'unarchiveCategory',
  'unarchiveTaxCategory',
  'setTaxPreset',
  'removeTaxPreset',
] as const;

beforeEach(() => {
  for (const method of MASTERDATA_METHODS) {
    vi
      .spyOn(MasterDataEndpointsService, method)
      .mockResolvedValue({} as never);
  }
});

afterEach(() => {
  vi.restoreAllMocks();
});

function httpError(status: number, message = 'Serverfehler'): ApiErrorClass {
  const request = {
    method: 'POST',
    url: '/api/v1/masterdata/accounts',
  } as unknown as ApiRequestOptions;
  const response = {
    url: '/api/v1/masterdata/accounts',
    status,
    statusText: 'ERR',
    data: null,
  } as unknown as ApiResult;
  return new ApiErrorClass(request, response, message);
}

function catalogStub(): CatalogService {
  return {
    allAccounts: signal([]),
    allPools: signal([]),
    allCategories: signal([]),
    allTaxCategories: signal([]),
    accounts: signal([]),
    categories: signal([]),
    taxCategories: signal([]),
    loading: signal(false),
    load: vi.fn().mockResolvedValue(undefined),
    refresh: vi.fn().mockResolvedValue(undefined),
  } as unknown as CatalogService;
}

function createForm(): MasterData {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [MasterData],
    providers: [
      {
        provide: Router,
        useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) },
      },
      { provide: CatalogService, useValue: catalogStub() },
    ],
  });
  return TestBed.createComponent(MasterData).componentInstance;
}

describe('MasterData — busy-Freigabe nach Schreiboperationen (Review R4 F2)', () => {
  it('setzt busy nach erfolgreicher Kontoanlage zurück; nächste Aktion bleibt möglich', async () => {
    const createAccount = vi.mocked(MasterDataEndpointsService.createAccount);
    const createPool = vi.mocked(MasterDataEndpointsService.createPool);
    createAccount.mockReset();
    createPool.mockReset();
    createAccount.mockResolvedValue({} as never);
    createPool.mockResolvedValue({} as never);

    const component = createForm();

    component.newAccountName = 'Giro';
    await component.createAccount();
    expect(createAccount).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false); // F2: vorher blieb true (Seite eingefroren)

    // Ohne den Fix würde dieser Aufruf early-returnen (busy steckfest).
    component.newPoolName = 'Flex';
    await component.createPool();
    expect(createPool).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false);
  });

  it('setzt busy nach erfolgreicher Kategorier- und Steuerkategorieanlage zurück', async () => {
    const createCategory = vi.mocked(MasterDataEndpointsService.createCategory);
    const createTaxCategory = vi.mocked(
      MasterDataEndpointsService.createTaxCategory,
    );
    createCategory.mockReset();
    createTaxCategory.mockReset();
    createCategory.mockResolvedValue({} as never);
    createTaxCategory.mockResolvedValue({} as never);

    const component = createForm();

    component.newCategoryName = 'Lebensmittel';
    await component.createCategory();
    expect(createCategory).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false);

    component.newTaxCategoryName = 'Vorsteuer';
    await component.createTaxCategory();
    expect(createTaxCategory).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false);
  });

  it('setzt busy nach erfolgreicher Archivierung zurück', async () => {
    const archiveAccount = vi.mocked(MasterDataEndpointsService.archiveAccount);
    archiveAccount.mockReset();
    archiveAccount.mockResolvedValue({} as never);

    const component = createForm();
    component.archive('account', 'acc-1', 'Giro');

    await vi.waitFor(() => expect(archiveAccount).toHaveBeenCalledTimes(1));
    // busy bleibt, bis die anschließende reload() abgeschlossen ist.
    await vi.waitFor(() => expect(component.busy()).toBe(false));
    expect(component.success()).toContain('archiviert');
  });

  it('setzt busy nach erfolgreicher Voreinstellung zurück', async () => {
    const setTaxPreset = vi.mocked(MasterDataEndpointsService.setTaxPreset);
    const removeTaxPreset = vi.mocked(MasterDataEndpointsService.removeTaxPreset);
    setTaxPreset.mockReset();
    removeTaxPreset.mockReset();
    setTaxPreset.mockResolvedValue({} as never);
    removeTaxPreset.mockResolvedValue({} as never);

    const component = createForm();

    component.openPreset('cat-1', 'Lebensmittel');
    await component.submitPreset();
    expect(setTaxPreset).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false);

    component.openPreset('cat-1', 'Lebensmittel');
    await component.removePreset();
    expect(removeTaxPreset).toHaveBeenCalledTimes(1);
    expect(component.busy()).toBe(false);
  });

  it('setzt busy auch bei fehlgeschlagener Anlage zurück (Fehler bleibt sichtbar)', async () => {
    const createAccount = vi.mocked(MasterDataEndpointsService.createAccount);
    createAccount.mockReset();
    createAccount.mockRejectedValueOnce(httpError(409, 'Name existiert'));

    const component = createForm();
    component.newAccountName = 'Giro';
    await component.createAccount();

    expect(component.busy()).toBe(false);
    expect(component.error()?.code).toBe('http_409');
    expect(component.success()).toBeNull();
  });
});
