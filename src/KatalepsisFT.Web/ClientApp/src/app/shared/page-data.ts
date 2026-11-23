import { DestroyRef, Signal, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap } from '@angular/router';
import { Store } from '@ngxs/store';
import { EMPTY, Observable, catchError, combineLatest, switchMap, tap } from 'rxjs';
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

  combineLatest([key, reload])
    .pipe(
      switchMap(([k]) => {
        const next = scope ? scope(k) : none;
        if (scope && asked !== none && next !== asked) {
          shown = none;
          data.set(null);
          failed.set(false);
        }

        asked = next;
        return load(k).pipe(
          tap((value) => {
            shown = k;
            data.set(value);
            failed.set(false);
          }),
          catchError(() => {
            if (shown !== k) {
              shown = none;
              data.set(null);
              failed.set(true);
            }

            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(inject(DestroyRef)),
    )
    .subscribe();
  return { data: data.asReadonly(), failed: failed.asReadonly() };
}

/** Loads a page's data again whenever its route parameters or the instrument filter change, or the fund's figures do. */
export function pageData<T>(load: (params: ParamMap, filter: InstrumentFilter) => Observable<T>): PageData<T> {
  const route = inject(ActivatedRoute);
  const store = inject(Store);
  return keptData(combineLatest([route.paramMap, store.select(ViewState.filter)]), toObservable(inject(LiveService).tick), ([params, filter]) => load(params, filter));
}
