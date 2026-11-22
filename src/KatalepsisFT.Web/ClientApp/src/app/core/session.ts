export interface Session {
  email: string;
  displayName: string;
  roles: string[];
  mustChangePassword: boolean;
}

export const ADMIN = 'Admin';

/** The message the API sent with an error, or a fallback when it sent none. */
export function apiMessage(error: unknown, fallback: string): string {
  const body = (error as { error?: { message?: string } } | null)?.error;
  return body?.message ?? fallback;
}
