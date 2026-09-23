import { useId, useMemo } from "react";
import type { Connection, ConnectionModel } from "./providers/contracts";

export type ConnectionModelPickerProps = {
  connections: Connection[];
  connectionId: string;
  models: ConnectionModel[];
  modelId: string;
  onConnectionChange: (connectionId: string) => void;
  onModelChange: (modelId: string) => void;
  connectionLabel?: string;
  modelLabel?: string;
  connectionDisabled?: boolean;
  modelDisabled?: boolean;
  modelsLoading?: boolean;
  className?: string;
};

function initials(name: string) {
  return name.split(/\s+/)
    .filter(Boolean)
    .map((word) => word[0])
    .join("")
    .slice(0, 2)
    .toUpperCase();
}

export function ConnectionModelPicker({
  connections,
  connectionId,
  models,
  modelId,
  onConnectionChange,
  onModelChange,
  connectionLabel = "New messages use",
  modelLabel = "Model",
  connectionDisabled = false,
  modelDisabled = false,
  modelsLoading = false,
  className,
}: ConnectionModelPickerProps) {
  const connectionSelectId = useId();
  const modelSelectId = useId();
  const selectedConnection = connections.find((connection) => connection.id === connectionId);
  const availableModels = useMemo(() => {
    const result = [...models];
    if (!result.some((model) => !model.id))
      result.unshift({ id: null, name: "Default" });
    if (modelId && !result.some((model) => model.id === modelId))
      result.push({ id: modelId, name: modelId });
    return result;
  }, [models, modelId]);

  return (
    <div className={["connection-model-picker", className].filter(Boolean).join(" ")}>
      <label className="section-label" htmlFor={connectionSelectId}>{connectionLabel}</label>
      <div className="connection-picker">
        <div className="connection-avatar">
          {selectedConnection ? initials(selectedConnection.name) : "AI"}
        </div>
        <select
          id={connectionSelectId}
          value={connectionId}
          onChange={(event) => onConnectionChange(event.target.value)}
          disabled={connectionDisabled}
        >
          {connections.map((connection) => (
            <option key={connection.id} value={connection.id}>{connection.name}</option>
          ))}
        </select>
      </div>

      {selectedConnection?.supportsModels && (
        <label className="model-picker" htmlFor={modelSelectId}>
          <span>{modelLabel}</span>
          <select
            id={modelSelectId}
            value={modelId}
            onChange={(event) => onModelChange(event.target.value)}
            disabled={modelsLoading || modelDisabled}
          >
            {availableModels.length > 0
              ? availableModels.map((model, index) => (
                  <option key={model.id || `default-${index}`} value={model.id || ""}>
                    {model.name}
                  </option>
                ))
              : <option value="">{modelsLoading ? "Loading models..." : "Default"}</option>}
          </select>
        </label>
      )}
    </div>
  );
}
