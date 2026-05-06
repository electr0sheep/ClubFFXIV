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
  /// DJ-authored description of the club. Shown in the public directory and
  /// in URL permission prompts. Empty string = no description. Capped at
  /// MAX_DESCRIPTION_LEN to keep the directory blob and ward-index entries
  /// reasonably sized.
  description?: string;
  djId: string;
  pubkey: string;
  updatedAt: number;
  door?: Door;
  /// Inclusion in the public directory (`GET /clubs`). Undefined / true = listed,
  /// false = hidden from the browse list. Hiding here does NOT hide the record
  /// from per-plot GET, ward proximity, or anyone who knows the plot key — it's
  /// strictly a directory-visibility flag.
  listed?: boolean;
}

export interface PublishBody {
  streamUrl: string;
  displayName: string;
  description?: string;
  nonce: number;
  door?: Door;
  listed?: boolean;
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
  description?: string;
  djId: string;
  door: Door;
  updatedAt: number;
}

/// Public directory: { plotKey: DirectoryEntry } stored at key `directory`.
/// Maintained alongside the per-club record on every publish/delete; mirrors
/// the ward-index pattern. Single blob keeps the browse endpoint to one GET.
export type DirectoryIndex = Record<string, DirectoryEntry>;

export interface DirectoryEntry {
  streamUrl: string;
  displayName: string;
  description?: string;
  djId: string;
  door?: Door;
  updatedAt: number;
}

export const DIRECTORY_KEY = "directory";

export const NONCE_MAX_AGE_MS = 5 * 60 * 1000;

export const MAX_DESCRIPTION_LEN = 500;
