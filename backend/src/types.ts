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
  /// Plaintext stream URL.
  /// - For unprotected clubs: the actual URL.
  /// - For v2-encrypted (passwordSalt + encUrl + encNonce): empty string —
  ///   the URL only exists ciphertext-side, so the registry literally
  ///   cannot leak it.
  /// - For legacy-protected (passwordSalt + passwordHash, no encUrl):
  ///   the actual URL, gated behind the passwordHash check on GET. Held
  ///   for back-compat with already-published records; new publishes
  ///   never take this path.
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
  /// Argon2id salt for password-protected clubs. Always present when the
  /// club is gated; consumed by both legacy (auth-hash) and v2 (encrypted-URL)
  /// flows. Safe to expose: bound to a high-entropy 6-word passphrase the
  /// registry never sees in plaintext.
  passwordSalt?: string;
  /// Legacy auth hash. Argon2id(passphrase, passwordSalt). Present only on
  /// records published before the v2 encryption rollout. New publishes
  /// never write this; v2 listeners decrypt encUrl directly without a
  /// server-side hash check, and the registry serves these records as-is
  /// for clients still on the legacy path.
  passwordHash?: string;
  /// AES-256-GCM ciphertext (with appended 16-byte tag) of the stream URL,
  /// base64-encoded. Encryption key derived client-side via
  /// Argon2id(passphrase, passwordSalt) → HKDF-SHA256(info="ClubFFXIV-url-enc-v1").
  /// When set together with encNonce + passwordSalt, this is a v2 record;
  /// the registry serves the ciphertext to anyone (the encryption is the
  /// access control, not a server-side hash gate).
  encUrl?: string;
  /// Base64-encoded 12-byte AES-GCM nonce paired with encUrl. Refreshed on
  /// every republish so a re-encryption never reuses (key, nonce).
  encNonce?: string;
  /// Record format version. 1 (or absent) = legacy plaintext-URL +
  /// auth-hash gate. 2 = encrypted URL. Bumped only on schema-affecting
  /// changes; cosmetic field additions don't increment this.
  schemaVersion?: number;
}

export interface PublishBody {
  /// Required when publishing without a password OR via the legacy
  /// password-hash flow. Empty string is accepted only for v2 encrypted
  /// publishes (encUrl + encNonce + passwordSalt set), where the actual
  /// URL travels in encUrl and the registry never sees plaintext.
  streamUrl: string;
  displayName: string;
  description?: string;
  nonce: number;
  door?: Door;
  listed?: boolean;
  /// Three valid combinations:
  ///   1. all absent — unprotected publish.
  ///   2. passwordSalt + passwordHash — legacy publish (still accepted on
  ///      POST for back-compat with old plugin builds).
  ///   3. passwordSalt + encUrl + encNonce + schemaVersion=2 — v2
  ///      encrypted publish. streamUrl must be empty in this case.
  /// Anything else is rejected as malformed.
  passwordSalt?: string;
  passwordHash?: string;
  encUrl?: string;
  encNonce?: string;
  schemaVersion?: number;
}

export interface DeleteBody {
  nonce: number;
}

/// Ward index: { plotKey: { streamUrl, displayName, djId, door } } stored at
/// key `ward:{worldId}:{territoryType}:{ward}`. One blob per ward keeps
/// listener queries to a single GET.
export type WardIndex = Record<string, WardIndexEntry>;

export interface WardIndexEntry {
  /// Empty string for password-protected entries (URL is gated behind the
  /// per-key GET with `?passwordHash=…`). passwordRequired flags this case
  /// so listeners know to prompt before trying to play.
  streamUrl: string;
  displayName: string;
  description?: string;
  djId: string;
  door: Door;
  updatedAt: number;
  passwordRequired?: boolean;
}

/// Public directory: { plotKey: DirectoryEntry } stored at key `directory`.
/// Maintained alongside the per-club record on every publish/delete; mirrors
/// the ward-index pattern. Single blob keeps the browse endpoint to one GET.
export type DirectoryIndex = Record<string, DirectoryEntry>;

export interface DirectoryEntry {
  /// Empty string for password-protected entries; see WardIndexEntry.streamUrl.
  streamUrl: string;
  displayName: string;
  description?: string;
  djId: string;
  door?: Door;
  updatedAt: number;
  passwordRequired?: boolean;
}

export const DIRECTORY_KEY = "directory";

export const NONCE_MAX_AGE_MS = 5 * 60 * 1000;

export const MAX_DESCRIPTION_LEN = 500;

/// Latest record format the registry knows how to write. Bumped when the
/// on-record schema changes meaningfully. Records may carry an older
/// version; serve them as-is — the upgrade happens on next republish.
export const CURRENT_SCHEMA_VERSION = 2;

/// Sanity bounds for v2 encrypted-URL fields. encUrl is base64 over a
/// (URL bytes + 16-byte GCM tag); even a 512-byte URL fits in ~700 base64
/// chars. encNonce is base64 over exactly 12 bytes → 16 chars. Generous
/// upper bounds catch obvious mis-sized inputs without locking us into
/// specific URL or AEAD parameters.
export const ENC_URL_MIN_LEN = 24;   // base64 of (1-byte URL + 16-byte tag)
export const ENC_URL_MAX_LEN = 1024;
export const ENC_NONCE_LEN = 16;     // base64 of exactly 12 bytes
