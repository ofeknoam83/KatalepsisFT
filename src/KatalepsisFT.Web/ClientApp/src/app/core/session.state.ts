import { Injectable, inject } from '@angular/core';
import { Action, Selector, State, StateContext } from '@ngxs/store';
import { catchError, of, tap } from 'rxjs';
import { AuthApi } from './auth.api';
import { ADMIN, Session } from './session';

export class LoadSession {
  static readonly type = '[Session] Load';
}

export class Login {
  static readonly type = '[Session] Login';
  constructor(
    readonly email: string,
    readonly password: string,
    readonly rememberMe: boolean,
  ) {}
}

export class ChangePassword {
  static readonly type = '[Session] Change password';
  constructor(
    readonly currentPassword: string,
    readonly newPassword: string,
  ) {}
}

export class Logout {
  static readonly type = '[Session] Logout';
}

export class SessionExpired {
  static readonly type = '[Session] Expired';
}

export interface SessionStateModel {
  /** False until the server has been asked once whether a session exists. */
  loaded: boolean;
  session: Session | null;
}

@State<SessionStateModel>({ name: 'session', defaults: { loaded: false, session: null } })
@Injectable()
export class SessionState {
  private readonly api = inject(AuthApi);

  @Selector()
  static loaded(state: SessionStateModel): boolean {
    return state.loaded;
  }

  @Selector()
  static session(state: SessionStateModel): Session | null {
    return state.session;
  }

  @Selector()
  static isAdmin(state: SessionStateModel): boolean {
    return state.session?.roles.includes(ADMIN) ?? false;
  }

  @Action(LoadSession)
  load(ctx: StateContext<SessionStateModel>) {
    return this.api.me().pipe(
      tap((session) => ctx.setState({ loaded: true, session })),
      // 401 simply means nobody is signed in.
      catchError(() => of(ctx.setState({ loaded: true, session: null }))),
    );
  }

  @Action(Login)
  login(ctx: StateContext<SessionStateModel>, action: Login) {
    return this.api.login(action.email, action.password, action.rememberMe).pipe(tap((session) => ctx.setState({ loaded: true, session })));
  }

  @Action(ChangePassword)
  changePassword(ctx: StateContext<SessionStateModel>, action: ChangePassword) {
    return this.api.changePassword(action.currentPassword, action.newPassword).pipe(tap((session) => ctx.setState({ loaded: true, session })));
  }

  @Action(Logout)
  logout(ctx: StateContext<SessionStateModel>) {
    return this.api.logout().pipe(tap(() => ctx.setState({ loaded: true, session: null })));
  }

  @Action(SessionExpired)
  expired(ctx: StateContext<SessionStateModel>) {
    ctx.setState({ loaded: true, session: null });
  }
}
