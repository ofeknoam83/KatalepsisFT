import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel } from '@microsoft/signalr';

/**
 * The browser's one live connection. The server says "fund N changed" and pages then reload their figures through the
 * ordinary API. While the socket is down, pages reload on a timer instead and the header says so.
 */
@Injectable({ providedIn: 'root' })
export class LiveService {
  /** How often pages reload when the socket is not connected. */
  private static readonly fallbackMs = 60_000;

  /** Goes up by one whenever the fund being watched should be read again. */
  readonly tick = signal(0);
  readonly connected = signal(false);

  private connection: HubConnection | null = null;
  private fundId: number | null = null;

  constructor() {
    setInterval(() => {
      if (!this.connected() && this.fundId !== null) {
        this.tick.update((t) => t + 1);
      }
    }, LiveService.fallbackMs);
  }

  /** Follows one fund; the previous one is dropped. */
  watch(fundId: number): void {
    if (this.fundId === fundId) {
      return;
    }

    const previous = this.fundId;
    this.fundId = fundId;
    const connection = this.ensure();
    if (connection.state === HubConnectionState.Connected) {
      if (previous !== null) {
        void connection.invoke('UnwatchFund', previous).catch(() => undefined);
      }

      void connection.invoke('WatchFund', fundId).catch(() => undefined);
    }
  }

  /** Called at sign-out, so the socket does not outlive the session. */
  stop(): void {
    this.fundId = null;
    this.connected.set(false);
    void this.connection?.stop();
    this.connection = null;
  }

  private ensure(): HubConnection {
    if (this.connection) {
      return this.connection;
    }

    // WebSockets only: the other transports need every request to reach the same server instance, and there are two.
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/live', { skipNegotiation: true, transport: HttpTransportType.WebSockets })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => Math.min(30_000, 1_000 * 2 ** Math.min(context.previousRetryCount, 5)) })
      .configureLogging(LogLevel.None)
      .build();
    connection.on('fundChanged', (fundId: number) => {
      if (fundId === this.fundId) {
        this.tick.update((t) => t + 1);
      }
    });
    connection.onreconnecting(() => this.connected.set(false));
    connection.onreconnected(() => this.joined(connection));
    connection.onclose(() => this.connected.set(false));
    this.connection = connection;
    connection
      .start()
      .then(() => this.joined(connection))
      .catch(() => this.connected.set(false));
    return connection;
  }

  /** After connecting or reconnecting: say which fund is watched, and reload once to catch what was missed meanwhile. */
  private joined(connection: HubConnection): void {
    this.connected.set(true);
    if (this.fundId !== null) {
      void connection.invoke('WatchFund', this.fundId).catch(() => undefined);
      this.tick.update((t) => t + 1);
    }
  }
}
