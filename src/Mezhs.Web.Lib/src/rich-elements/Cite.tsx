import type { RichProps } from "./Element";
// The API supplies no trusted citation URLs; render an inert source indicator.
export function Cite(_props: RichProps) {
  return <span className="rich-citation-unavailable" role="note"
    title="Source reference unavailable in this MEŽS transcript">[source unavailable]</span>;
}
