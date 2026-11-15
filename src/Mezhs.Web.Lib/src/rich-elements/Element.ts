import type { ReactNode } from "react";
// Presentation props only. Attribute syntax and validation are exclusively C#-owned.
export type RichProps = { children?: ReactNode; [key: string]: unknown };
