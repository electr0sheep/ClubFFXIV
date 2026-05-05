// Workers' Web Crypto supports Ed25519 verify natively.

export async function verifySignature(
  message: string,
  signatureB64: string,
  pubkeyB64: string,
): Promise<boolean> {
  try {
    const pubkeyBytes = base64ToBytes(pubkeyB64);
    const sigBytes = base64ToBytes(signatureB64);
    const messageBytes = new TextEncoder().encode(message);

    if (pubkeyBytes.length !== 32 || sigBytes.length !== 64) return false;

    const key = await crypto.subtle.importKey(
      "raw",
      pubkeyBytes,
      { name: "Ed25519" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify("Ed25519", key, sigBytes, messageBytes);
  } catch {
    return false;
  }
}

export async function djIdFromPubkey(pubkeyB64: string): Promise<string> {
  const buf = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(pubkeyB64),
  );
  return bytesToHex(new Uint8Array(buf));
}

function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
  return arr;
}

function bytesToHex(bytes: Uint8Array): string {
  let out = "";
  for (const b of bytes) out += b.toString(16).padStart(2, "0");
  return out;
}
