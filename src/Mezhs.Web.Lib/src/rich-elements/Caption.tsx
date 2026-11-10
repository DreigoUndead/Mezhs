import { defineElement, type RichProps } from "./Element";
export const captionElement = defineElement("caption", [],
  ({ children }: RichProps) => <span className="rich-caption">{children}</span>);
