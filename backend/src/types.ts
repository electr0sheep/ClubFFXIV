export interface Env {
  CLUBS_KV: KVNamespace;
}

export interface Door {
  x: number;
  y: number;
  z: number;
  territoryType: number; // OUTDOOR ward territory
  ward: number;
}

export interface ClubRecord {
  streamUrl: string;
  displayName: string;
  djId: string;
  pubkey: string;
  updatedAt: number;
  door?: Door;
}

export interface PublishBody {
  streamUrl: string;
  displayName: string;
  nonce: number;
  door?: Door;
}

export interface DeleteBody {
  nonce: number;
}

/// Ward index: { plotKey: { streamUrl, displayName, djId, door } } stored at
/// key `ward:{worldId}:{territoryType}:{ward}`. One blob per ward keeps
/// listener queries to a single GET.
export type WardIndex = Record<string, WardIndexEntry>;

export interface WardIndexEntry {
  streamUrl: string;
  displayName: string;
  djId: string;
  door: Door;
  updatedAt: number;
}

export const NONCE_MAX_AGE_MS = 5 * 60 * 1000;
