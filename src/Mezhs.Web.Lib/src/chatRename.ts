const maxChatTitleLength = 200;

// Keep the inserted text and discard only the excess at the end.
export function readChatRenameValue(input: HTMLInputElement): string {
  const value = input.value.slice(0, maxChatTitleLength);
  if (value !== input.value) {
    const start = input.selectionStart ?? value.length;
    const end = input.selectionEnd ?? start;
    const direction = input.selectionDirection ?? "none";
    input.value = value;
    input.setSelectionRange(Math.min(start, value.length), Math.min(end, value.length), direction);
  }
  return value;
}
