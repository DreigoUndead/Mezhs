import { defineElement, type RichProps } from "./Element";
import { read, tone } from "./Style";
export const titleElement = defineElement("title", ["size","color","textAlign","tabularNums","weight","strong"],
  ({ children, ...props }: RichProps) => {
    const size = read(props, "size");
    return <span className={"rich-title" + tone(props)} style={{
      fontSize: ({ xs: ".9em", sm: "1em", md: "1.12em", lg: "1.3em", xl: "1.55em" } as Record<string, string>)[size],
      fontVariantNumeric: read(props, "tabularNums") === "true" ? "tabular-nums" : undefined,
      textAlign: read(props, "textAlign") === "center" ? "center" : undefined,
    }}>{children}</span>;
  });
