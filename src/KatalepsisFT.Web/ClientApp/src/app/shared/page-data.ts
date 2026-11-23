import { DestroyRef, Signal, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap } from '@angular/router';
import { Store } from '@ngxs/store';
import { Observable, Subscription, combineLatest } from 'rxjs';
import { LiveService } from '../core/live.service';
import { InstrumentFilter } from '../funds/models';
import { ViewState } from '../funds/view.state';

export interface PageData<T> {
  data: Signal<T | null>;
  failed: Signal<boolean>;
}

/**
 * Loads data for what `key` says, and again whenever `reload` emits. Must be called while a component is being built.
 *
 * A reload that fails keeps the last good data on screen: one lost request must not blank a page or close a form
 * someone is typing in. The error state is only for a load that has nothing of its own to show, which is the first
 * load and the first one after `key` changed.
 *
 * `scope` picks the part of the key that says whose figures these are. When it changes the old data is dropped at
 * once, so one sleeve's figures are never shown under another's name while the new ones load.
 */
export function keptData<K, T>(key: Observable<K>, reload: Observable<unknown>, load: (key: K) => Observable<T>, scope?: (key: K) => unknown): PageData<T> {
  const data = signal<T | null>(null);
  const failed = signal(false);
  const none = {};
  // The key the data on screen was loaded for, and the scope last asked for.
  let shown: unknown = none;
  let asked: unknown = none;

  // The load in flight, the key it is for, and whether another reload was asked for meanwhile.
  let running: Subscription | null = null;
  let runningKey: unknown = none;
  let again = false;

  const start = (k: K): void => {
    running?.unsubscribe();
    runningKey = k;
    again = false;
    const next = scope ? scope(k) : none;
    if (scope && asked !== none && next !== asked) {
      shown = none;
      data.set(null);
      failed.set(false);
    }

    asked = next;
    let done = false;
    const finish = (): void => {
      done = true;
      running = null;
      if (again) {
        start(k);
      }
    };
    const load$ = load(k).subscribe({
      next: (value) => {
        shown = k;
        data.set(value);
        failed.set(false);
      },
      error: () => {
        if (shown !== k) {
          shown = none;
          data.set(null);
          failed.set(true);
        }

        finish();
      },
      complete: finish,
    });
    // A load that answered at once has already finished; do not mark it as still running.
    running = done ? null : load$;
  };

  // Figures can change every couple of seconds. A reload for the same key waits for the load in flight and then runs
  // once: cancelling and restarting on every change would mean a slow request never gets to answer at all.
  const changes = combineLatest([key, reload]).subscribe(([k]) => {
    if (running !== null && k === runningKey) {
      again = true;
      return;
    }

    start(k);
  });
  inject(DestroyRef).onDestroy(() => {
    changes.unsubscribe();
    running?.unsubscribe();
  });
  return { data: data.asReadonly(), failed: failed.asReadonly() };
}

/** Loads a page's data again whenever its route parameters or the instrument filter change, or the fund's figures do. */
export function pageData<T>(load: (params: ParamMap, filter: InstrumentFilter) => Observable<T>): PageData<T> {
  const route = inject(ActivatedRoute);
  const store = inject(Store);
  return keptData(combineLatest([route.paramMap, store.select(ViewState.filter)]), toObservable(inject(LiveService).tick), ([params, filter]) => load(params, filter));
}
