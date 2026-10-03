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

export function ConnectionModelPicker({
  connections,
  connectionId,
  models,
  modelId,
  onConnectionChange,
  onModelChange,
  connectionLabel = "Integration",
  modelLabel = "Model / effort",
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

  const modelUnavailable = !selectedConnection?.supportsModels;
  const resolvedModelDisabled = modelDisabled || modelsLoading || modelUnavailable || !connectionId;

  return (
    <div className={["connection-model-picker", className].filter(Boolean).join(" ")}>
      <label className="target-picker-field" htmlFor={connectionSelectId}>
        <span>{connectionLabel}</span>
        <select
          id={connectionSelectId}
          value={connectionId}
          onChange={(event) => onConnectionChange(event.target.value)}
          disabled={connectionDisabled}
        >
          {connections.length === 0 && <option value="">No integrations available</option>}
          {connections.map((connection) => (
            <option key={connection.id} value={connection.id}>{connection.name}</option>
          ))}
        </select>
      </label>

      <label className="target-picker-field" htmlFor={modelSelectId}>
        <span>{modelLabel}</span>
        <select
          id={modelSelectId}
          value={modelUnavailable ? "" : modelId}
          onChange={(event) => onModelChange(event.target.value)}
          disabled={resolvedModelDisabled}
        >
          {modelUnavailable
            ? <option value="">Not supported</option>
            : modelsLoading
              ? <option value={modelId}>{modelId || "Loading models..."}</option>
              : availableModels.length > 0
                ? availableModels.map((model, index) => (
                    <option key={model.id || `default-${index}`} value={model.id || ""}>
                      {model.name}
                    </option>
                  ))
                : <option value="">Default</option>}
        </select>
      </label>
    </div>
  );
}
