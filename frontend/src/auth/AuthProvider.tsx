import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';

const storageKey = 'ers.auth.session';
const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/+$/, '');

export interface CurrentUser {
  userId: number;
  tenantId: number;
  tenantName?: string | null;
  fullName: string;
  email: string;
  roles: string[];
}

interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresAt: string;
}

interface PersistedSession {
  accessToken: string;
  expiresAt: number;
}

interface AuthState {
  status: 'loading' | 'anonymous' | 'authenticated' | 'unavailable';
  accessToken: string | null;
  expiresAt: number | null;
  user: CurrentUser | null;
  notice: string | null;
}

interface AuthContextValue extends AuthState {
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => Promise<boolean>;
  clearNotice: () => void;
}

type ApiFailure = 'credentials' | 'unavailable' | 'invalid-response' | 'expired';

export class AuthenticationError extends Error {
  constructor(readonly failure: ApiFailure) {
    super(failure);
    this.name = 'AuthenticationError';
  }
}

const AuthContext = createContext<AuthContextValue | null>(null);

function readSession(): PersistedSession | null {
  const stored = sessionStorage.getItem(storageKey);
  if (!stored) return null;

  try {
    const candidate = JSON.parse(stored) as Partial<PersistedSession>;
    if (
      typeof candidate.accessToken === 'string' &&
      typeof candidate.expiresAt === 'number' &&
      Number.isFinite(candidate.expiresAt) &&
      candidate.expiresAt > Date.now()
    ) {
      return {
        accessToken: candidate.accessToken,
        expiresAt: candidate.expiresAt,
      };
    }
  } catch {
    sessionStorage.removeItem(storageKey);
    return null;
  }

  sessionStorage.removeItem(storageKey);
  return null;
}

function saveSession(session: PersistedSession | null) {
  if (session) {
    sessionStorage.setItem(storageKey, JSON.stringify(session));
  } else {
    sessionStorage.removeItem(storageKey);
  }
}

function isCurrentUser(value: unknown): value is CurrentUser {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<CurrentUser>;
  return (
    Number.isInteger(candidate.userId) &&
    Number.isInteger(candidate.tenantId) &&
    (candidate.tenantName === undefined ||
      candidate.tenantName === null ||
      typeof candidate.tenantName === 'string') &&
    typeof candidate.fullName === 'string' &&
    typeof candidate.email === 'string' &&
    Array.isArray(candidate.roles) &&
    candidate.roles.every((role) => typeof role === 'string')
  );
}

async function getProfile(accessToken: string): Promise<CurrentUser> {
  let response: Response;
  try {
    response = await fetch(`${apiBaseUrl}/api/auth/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
  } catch {
    throw new AuthenticationError('unavailable');
  }

  if (response.status === 401) throw new AuthenticationError('expired');
  if (!response.ok) throw new AuthenticationError('unavailable');

  const user: unknown = await response.json().catch(() => null);
  if (!isCurrentUser(user)) throw new AuthenticationError('invalid-response');
  return user;
}

const initialSession = readSession();

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({
    status: 'loading',
    accessToken: initialSession?.accessToken ?? null,
    expiresAt: initialSession?.expiresAt ?? null,
    user: null,
    notice: null,
  });

  useEffect(() => {
    if (!initialSession) {
      setState((current) => ({ ...current, status: 'anonymous' }));
      return;
    }

    const controller = new AbortController();
    void getProfile(initialSession.accessToken)
      .then((user) => {
        if (controller.signal.aborted) return;
        setState({
          status: 'authenticated',
          accessToken: initialSession.accessToken,
          expiresAt: initialSession.expiresAt,
          user,
          notice: null,
        });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        if (error instanceof AuthenticationError && error.failure === 'unavailable') {
          setState((current) => ({
            ...current,
            status: 'unavailable',
            notice: 'We could not verify your session. Check your connection and try again.',
          }));
          return;
        }

        saveSession(null);
        setState({
          status: 'anonymous',
          accessToken: null,
          expiresAt: null,
          user: null,
          notice: 'Your session has expired. Sign in again to continue.',
        });
      });

    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (state.status !== 'authenticated' || state.expiresAt === null) return;

    const remaining = state.expiresAt - Date.now();
    if (remaining <= 0) {
      saveSession(null);
      setState({
        status: 'anonymous',
        accessToken: null,
        expiresAt: null,
        user: null,
        notice: 'Your session has expired. Sign in again to continue.',
      });
      return;
    }

    const timeout = window.setTimeout(() => {
      saveSession(null);
      setState({
        status: 'anonymous',
        accessToken: null,
        expiresAt: null,
        user: null,
        notice: 'Your session has expired. Sign in again to continue.',
      });
    }, remaining);
    return () => window.clearTimeout(timeout);
  }, [state.expiresAt, state.status]);

  const signIn = useCallback(
    async (email: string, password: string) => {
      let response: Response;
      try {
        response = await fetch(`${apiBaseUrl}/api/auth/login`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email, password }),
        });
      } catch {
        throw new AuthenticationError('unavailable');
      }

      if (response.status === 401) throw new AuthenticationError('credentials');
      if (response.status === 400) throw new AuthenticationError('credentials');
      if (!response.ok) throw new AuthenticationError('unavailable');

      const payload: unknown = await response.json().catch(() => null);
      if (!payload || typeof payload !== 'object') {
        throw new AuthenticationError('invalid-response');
      }

      const result = payload as Partial<LoginResponse>;
      const expiresAt = typeof result.expiresAt === 'string' ? Date.parse(result.expiresAt) : NaN;
      if (
        typeof result.accessToken !== 'string' ||
        result.accessToken.length === 0 ||
        result.tokenType?.toLowerCase() !== 'bearer' ||
        !Number.isFinite(expiresAt) ||
        expiresAt <= Date.now()
      ) {
        throw new AuthenticationError('invalid-response');
      }

      let user: CurrentUser;
      try {
        user = await getProfile(result.accessToken);
      } catch (error) {
        if (error instanceof AuthenticationError) throw error;
        throw new AuthenticationError('unavailable');
      }

      saveSession({ accessToken: result.accessToken, expiresAt });
      setState({
        status: 'authenticated',
        accessToken: result.accessToken,
        expiresAt,
        user,
        notice: null,
      });
    },
    [],
  );

  const signOut = useCallback(async () => {
    let acknowledged = false;
    if (state.accessToken) {
      try {
        const response = await fetch(`${apiBaseUrl}/api/auth/logout`, {
          method: 'POST',
          headers: { Authorization: `Bearer ${state.accessToken}` },
        });
        acknowledged = response.status === 204;
      } catch {
        acknowledged = false;
      }
    }

    saveSession(null);
    setState({
      status: 'anonymous',
      accessToken: null,
      expiresAt: null,
      user: null,
      notice: acknowledged
        ? 'You have been signed out.'
        : 'You have been signed out on this device. The server could not confirm the request.',
    });
    return acknowledged;
  }, [state.accessToken]);

  const clearNotice = useCallback(() => {
    setState((current) => ({ ...current, notice: null }));
  }, []);

  const value = useMemo(
    () => ({ ...state, signIn, signOut, clearNotice }),
    [state, signIn, signOut, clearNotice],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within an AuthProvider.');
  return context;
}
