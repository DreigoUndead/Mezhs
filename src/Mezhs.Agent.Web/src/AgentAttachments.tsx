import { useEffect, useRef, useState, type RefObject } from "react";
import { apiJson, type ApiFile, type IntegrationCapabilities } from "@mezhs/web-lib";

export type PendingAttachment = {
  key: string;
  file: File;
  connectionId: string;
  previewUrl?: string;
  uploading: boolean;
  uploaded?: ApiFile;
  error?: string;
};

const allowedExtensions = /\.(txt|md|csv|pdf|docx|png)$/i;

export function useAgentAttachments() {
  const [pending, setPending] = useState<PendingAttachment[]>([]);
  const pendingRef = useRef<PendingAttachment[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);
  const nextKey = useRef(0);

  function update(change: (files: PendingAttachment[]) => PendingAttachment[]) {
    pendingRef.current = change(pendingRef.current);
    setPending(pendingRef.current);
  }

  function clear() {
    for (const item of pendingRef.current)
      if (item.previewUrl) URL.revokeObjectURL(item.previewUrl);
    pendingRef.current = [];
    setPending([]);
    if (inputRef.current) inputRef.current.value = "";
  }

  useEffect(() => () => {
    for (const item of pendingRef.current)
      if (item.previewUrl) URL.revokeObjectURL(item.previewUrl);
  }, []);

  function remove(key: string) {
    update((current) => {
      const removed = current.find((item) => item.key === key);
      if (removed?.previewUrl) URL.revokeObjectURL(removed.previewUrl);
      return current.filter((item) => item.key !== key);
    });
  }

  async function select(files: FileList | null, connectionId: string, capabilities?: IntegrationCapabilities) {
    const selected = Array.from(files ?? []);
    if (inputRef.current) inputRef.current.value = "";
    if (!connectionId || !capabilities?.fileInput) return;

    // Register all selections before the first await: even the last file blocks Send.
    const queued = selected.map((file) => {
      const isImage = /\.png$/i.test(file.name) || file.type.startsWith("image/");
      const error = !allowedExtensions.test(file.name)
        ? "Supported files: TXT, MD, CSV, PDF, DOCX and PNG."
        : isImage && !capabilities.imageInput
          ? "This connection does not support image input."
          : undefined;
      return {
        key: String(++nextKey.current), file, connectionId,
        previewUrl: isImage && !error ? URL.createObjectURL(file) : undefined,
        uploading: !error, error,
      };
    });
    update((current) => [...current, ...queued]);
    for (const { file, key, error } of queued) {
      if (error) continue;
      const form = new FormData();
      form.append("connectionId", connectionId);
      form.append("file", file, file.name);
      try {
        const uploaded = await apiJson<ApiFile>("", "/v1/files", {
          method: "POST",
          body: form,
        });
        update((current) => current.map((item) => item.key !== key ? item : {
          ...item,
          uploading: false,
          ...(uploaded.connectionId === connectionId
            ? { uploaded }
            : { error: "Uploaded file belongs to a different connection." }),
        }));
      } catch (failure) {
        const message = failure instanceof Error ? failure.message : "Upload failed.";
        update((current) => current.map((item) =>
          item.key === key ? { ...item, uploading: false, error: message } : item));
      }
    }
  }

  function submissionFiles(connectionId: string): string[] | null {
    if (pendingRef.current.some((item) =>
      item.connectionId !== connectionId || !item.uploaded || !!item.error))
      return null;
    return pendingRef.current.map((item) => item.uploaded!.fileId);
  }

  return { pending, inputRef, select, remove, clear, submissionFiles };
}

export function AttachmentPicker({ inputRef, onSelect, disabled, capabilities }: {
  inputRef: RefObject<HTMLInputElement>;
  onSelect: (files: FileList | null) => void;
  disabled: boolean;
  capabilities?: IntegrationCapabilities;
}) {
  const canAttach = !disabled && !!capabilities?.fileInput;
  return (
    <>
      <input ref={inputRef} className="file-input" type="file"
        accept=".txt,.md,.csv,.pdf,.docx,.png" multiple
        aria-label="Select attachments"
        disabled={!canAttach}
        onChange={(event) => onSelect(event.target.files)} />
      <button type="button" className="attach" aria-label="Attach files"
        disabled={!canAttach}
        title={capabilities?.fileInput ? "Attach files" : "This connection does not support files"}
        onClick={() => inputRef.current?.click()}>+</button>
    </>
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
          {item.previewUrl ? <img src={item.previewUrl} alt="" /> : <b>FILE</b>}
          <span title={item.file.name}>{item.file.name}
            <small role={item.error ? "alert" : undefined}>
              {item.error || (item.uploading ? "Uploading..." : "Uploaded")}
            </small>
          </span>
          <button type="button" disabled={disabled} onClick={() => onRemove(item.key)}
            aria-label={"Remove " + item.file.name}>x</button>
        </div>
      ))}
    </div>
  );
}
