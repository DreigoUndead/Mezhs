import type { RichProps } from "./Element";
import { read } from "./Style";
export const linkComponent = ({ children, ...props }: RichProps) => {
    const url = read(props, "url");
    const label = children || read(props, "title") || url;
    return /^(https?:\/\/|mailto:)/i.test(url)
      ? <a href={url} rel="noopener noreferrer" target="_blank">{label}</a>
      : <span>{label}</span>;
  };
