import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Session } from './session';

@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);

  me(): Observable<Session> {
    return this.http.get<Session>('/api/auth/me');
  }

  login(email: string, password: string, rememberMe: boolean): Observable<Session> {
    return this.http.post<Session>('/api/auth/login', { email, password, rememberMe });
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', null);
  }

  changePassword(currentPassword: string, newPassword: string): Observable<Session> {
    return this.http.post<Session>('/api/auth/change-password', { currentPassword, newPassword });
  }
}
