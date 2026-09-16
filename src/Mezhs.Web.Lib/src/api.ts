import { useEffect, useSyncExternalStore } from "react";

export type ApiAvailability = "checking" | "online" | "offline";

type ApiState = {
  availability: ApiAvailability;
  listeners: Set<() => void>;
  recovery?: Promise<void>;
};

const states = new Map<string, ApiState>();
const unavailableStatuses = new Set([502, 503, 504]);
const retryDelayMs = 500;

function normalizeBase(apiBase: string) {
  return apiBase.replace(/\/+$/, "");
}

function stateFor(apiBase: string) {
  const key = normalizeBase(apiBase);
  let state = states.get(key);
  if (!state) {
    state = {
      availability: "checking",
      listeners: new Set(),
    };
    states.set(key, state);
  }
  return state;
}

function endpoint(apiBase: string, path: string) {
  const base = normalizeBase(apiBase);
  if (!path.startsWith("/")) path = `/${path}`;
  return `${base}${path}`;
}

function setAvailability(state: ApiState, availability: ApiAvailability) {
  if (state.availability === availability) return;
  state.availability = availability;
  state.listeners.forEach((listener) => listener());
}

function delay(milliseconds: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds));
}

async function recover(apiBase: string, state: ApiState) {
  if (!state.recovery) {
    state.recovery = (async () => {
      let firstAttempt = true;
      while (true) {
        if (!firstAttempt)
          await delay(retryDelayMs);
        firstAttempt = false;

        try {
          const response = await fetch(endpoint(apiBase, "/health"), {
            cache: "no-store",
          });
          if (response.ok) {
            setAvailability(state, "online");
            return;
          }
        } catch {
          // Availability is represented by the shared state below.
        }

        setAvailability(state, "offline");
      }
    })().finally(() => {
      state.recovery = undefined;
    });
  }

  await state.recovery;
}

export async function waitForApi(apiBase: string) {
  const state = stateFor(apiBase);
  if (state.availability === "online") return;
  await recover(apiBase, state);
}

function isRead(init?: RequestInit) {
  const method = (init?.method ?? "GET").toUpperCase();
  return method === "GET" || method === "HEAD";
}

export async function apiFetch(
  apiBase: string,
  path: string,
  init?: RequestInit,
): Promise<Response> {
  const state = stateFor(apiBase);
  const read = isRead(init);

  while (true) {
    if (read)
      await waitForApi(apiBase);

    try {
      const response = await fetch(endpoint(apiBase, path), init);
      if (unavailableStatuses.has(response.status)) {
        setAvailability(state, "offline");
        if (read) {
          await delay(retryDelayMs);
          continue;
        }
        return response;
      }

      setAvailability(state, "online");
      return response;
    } catch (error) {
      if (init?.signal?.aborted)
        throw error;

      setAvailability(state, "offline");
      if (!read)
        throw error;
    }
  }
}

export async function apiJson<T>(
  apiBase: string,
  path: string,
  init?: RequestInit,
): Promise<T> {
  return expectJson<T>(await apiFetch(apiBase, path, init));
}

export async function apiJsonOrEmpty(
  apiBase: string,
  path: string,
  init?: RequestInit,
): Promise<void> {
  const response = await apiFetch(apiBase, path, init);
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
}

export async function expectJson<T = unknown>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.json() as Promise<T>;
}

export function useApiAvailability(apiBase: string): ApiAvailability {
  const state = stateFor(apiBase);
  const availability = useSyncExternalStore(
    (listener) => {
      state.listeners.add(listener);
      return () => state.listeners.delete(listener);
    },
    () => state.availability,
    () => state.availability,
  );

  useEffect(() => {
    void waitForApi(apiBase);
  }, [apiBase]);

  return availability;
}
