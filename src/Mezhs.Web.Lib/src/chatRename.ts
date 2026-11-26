const maxChatTitleLength = 200;

// Keep the inserted text and discard only the excess at the end.
export function readChatRenameValue(input: HTMLInputElement): string {
  if (input.value.length > maxChatTitleLength) {
    input.setRangeText("", maxChatTitleLength, input.value.length, "preserve");
  }
  return input.value;
}
