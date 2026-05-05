export interface Env {
  CLUBS_KV: KVNamespace;
}

export interface ClubRecord {
  streamUrl: string;
  displayName: string;
  djId: string;
  pubkey: string;
  updatedAt: number;
}

export interface PublishBody {
  streamUrl: string;
  displayName: string;
  nonce: number;
}

export interface DeleteBody {
  nonce: number;
}

// Anti-replay window for signed requests.
export const NONCE_MAX_AGE_MS = 5 * 60 * 1000;
