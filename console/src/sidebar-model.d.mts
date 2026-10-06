export type SidebarGroup = {
  id: string;
  title: string;
  unavailable?: boolean;
  matchPaths?: string[];
  items: [string, string][];
};
export const sidebarGroups: SidebarGroup[];
export function isSidebarItemActive(path: string, url: string): boolean;
export function sidebarGroupForPath(path: string): string | null;
export function toggleSidebarGroup(expanded: string | null, selected: string): string | null;
