import { NavigationError } from '@angular/router';

const KEY = 'kft:reloaded-for-new-version';

/** How long after one reload a second failure is taken as a real fault instead of another new version. */
const QUIET_MS = 15_000;

/**
 * After a release the files of the previous version are gone from the server. A tab still running the previous
 * version then fails the moment it opens a page it had not loaded yet, and would sit on a blank area. Loading the
 * address afresh picks up the new version. It is done once: if the page still cannot load, the fault is something
 * else and reloading again would only loop.
 */
export function reloadWhenAppIsStale(error: NavigationError): void {
  const message = String((error.error as { message?: unknown } | null)?.message ?? error.error ?? '');
  const missingFile = /dynamically imported module|Importing a module script failed|error loading dynamically imported|ChunkLoadError|Loading chunk/i.test(message);
  if (!missingFile) {
    return;
  }

  let last = 0;
  try {
    last = Number(sessionStorage.getItem(KEY) ?? 0);
    sessionStorage.setItem(KEY, String(Date.now()));
  } catch {
    // Storage is unavailable in some private modes; without it we cannot tell a loop from a first try, so we do not reload.
    return;
  }

  if (Date.now() - last > QUIET_MS) {
    window.location.assign(error.url);
  }
}
