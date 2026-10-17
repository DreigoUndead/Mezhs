import { useRef, useState } from "react";
import { apiJson, type ApiFile, type IntegrationCapabilities } from "@mezhs/web-lib";

export type PendingAttachment = {
  key: string;
  path: string;
  connectionId: string;
  uploading: boolean;
  uploaded?: ApiFile;
  error?: string;
};

export function useAgentAttachments() {
  const [pending, setPending] = useState<PendingAttachment[]>([]);
  const pendingRef = useRef<PendingAttachment[]>([]);
  const nextKey = useRef(0);

  function update(change: (files: PendingAttachment[]) => PendingAttachment[]) {
    pendingRef.current = change(pendingRef.current);
    setPending(pendingRef.current);
  }

  function clear() {
    pendingRef.current = [];
    setPending([]);
  }

  function remove(key: string) {
    update((current) => current.filter((item) => item.key !== key));
  }

  async function select(path: string, connectionId: string) {
    if (!path.trim() || !connectionId) return;
    const key = String(++nextKey.current);
    update((current) => [...current, { key, path, connectionId, uploading: true }]);
    try {
      const uploaded = await apiJson<ApiFile>("", "/v1/files", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ connectionId, path }),
      });
      update((current) => current.map((item) =>
        item.key === key ? { ...item, uploading: false, uploaded } : item));
    } catch (failure) {
      update((current) => current.map((item) =>
        item.key === key ? {
          ...item, uploading: false,
          error: failure instanceof Error ? failure.message : "Upload failed.",
        } : item));
    }
  }

  function submissionFiles(connectionId: string): string[] | null {
    if (pendingRef.current.some((item) =>
      item.connectionId !== connectionId || !item.uploaded || item.error))
      return null;
    return pendingRef.current.map((item) => item.uploaded!.fileId);
  }

  return { pending, select, remove, clear, submissionFiles };
}

export function AttachmentPicker({ onSelect, disabled, capabilities }: {
  onSelect: (path: string) => void;
  disabled: boolean;
  capabilities?: IntegrationCapabilities;
}) {
  const [path, setPath] = useState("");
  const canAttach = !disabled && !!capabilities?.fileInput;
  function attach() {
    if (!path.trim()) return;
    onSelect(path.trim());
    setPath("");
  }
  return (
    <div className="local-file-picker">
      <input type="text" aria-label="Local file path" placeholder="File path on MEŽS machine"
        value={path} onChange={(event) => setPath(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); attach(); } }}
        disabled={!canAttach} />
      <button type="button" className="attach" aria-label="Upload local file"
        disabled={!canAttach || !path.trim()} onClick={attach}>+</button>
    </div>
  );
}

export function PendingAttachmentList({ pending, onRemove, disabled }: {
  pending: PendingAttachment[];
  onRemove: (key: string) => void;
  disabled: boolean;
}) {
  if (!pending.length) return null;
  return (
    <div className="pending-files">
      {pending.map((item) => (
        <div className={"pending-file " + (item.error ? "failed" : "")} key={item.key}>
          <b>FILE</b>
          <span title={item.path}>{item.uploaded?.name || item.path}
            <small role={item.error ? "alert" : undefined}>
              {item.error || (item.uploading ? "Uploading..." : "Uploaded")}
            </small>
          </span>
          <button type="button" disabled={disabled} onClick={() => onRemove(item.key)}
            aria-label={"Remove " + item.path}>x</button>
        </div>
      ))}
    </div>
  );
}
