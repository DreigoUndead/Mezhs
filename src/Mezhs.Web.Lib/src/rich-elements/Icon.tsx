import { Check, CircleCheck, CircleAlert, CircleX, Info, ArrowDown, ArrowRight, GitBranch, GitPullRequest, ScrollText, ChartNoAxesCombined, Network, Database } from "lucide-react";
import { defineElement, type RichProps } from "./Element";
import { read, tone } from "./Style";
const icons = {
  check: Check, "check-circle": CircleCheck, "circle-check": CircleCheck,
  "alert-circle": CircleAlert, "x-circle": CircleX, info: Info,
  "arrow-down": ArrowDown, "arrow-right": ArrowRight, "git-branch": GitBranch, "git-pull-request": GitPullRequest,
  "scroll-text": ScrollText, "chart-no-axes-combined": ChartNoAxesCombined,
  network: Network, database: Database,
};


export const iconElement = defineElement("icon", ["name","size","color","inline"],
  (props: RichProps) => {
    const name = read(props, "name");
    const Icon = Object.prototype.hasOwnProperty.call(icons, name)
      ? icons[name as keyof typeof icons] : undefined;
    const size = read(props, "size") === "lg" ? 20 : read(props, "size") === "sm" ? 14 : 16;
    return <span className={"rich-icon" + tone(props)} role="img" aria-label={name || "icon"}>
      {Icon ? <Icon size={size} aria-hidden="true" /> : name}
    </span>;
  });
