import type { RichProps } from "./Element";
import { read } from "./Style";
export const dividerComponent = (props: RichProps) => <hr className={"rich-divider" + (read(props, "color") === "strong" ? " rich-divider-strong" : "")} />;
