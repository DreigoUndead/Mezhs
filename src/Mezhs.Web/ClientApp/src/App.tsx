import { useEffect, useState } from "react";
import { MezhsChatApp } from "@mezhs/web-lib";

type AppConfig = {
  apiBaseUrl: string;
};

export default function App() {
  const [apiBaseUrl, setApiBaseUrl] = useState<string>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    void fetch("/app-config")
      .then(async (response) => {
        if (!response.ok) throw new Error(`Configuration request failed (${response.status}).`);
        return response.json() as Promise<AppConfig>;
      })
      .then((config) => setApiBaseUrl(config.apiBaseUrl))
      .catch((reason) => setError(
        reason instanceof Error ? reason.message : "Could not load the MEŽS web configuration.",
      ));
  }, []);

  if (error) return <main className="bootstrap-state">{error}</main>;
  if (!apiBaseUrl) return <main className="bootstrap-state">Loading MEŽS…</main>;
  return <MezhsChatApp apiBaseUrl={apiBaseUrl} />;
}
