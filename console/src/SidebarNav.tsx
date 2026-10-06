import {
  IconLayoutDashboard, IconSitemap, IconCode, IconAdjustments, IconShieldCheck,
  IconBox, IconRocket, IconServer, IconChartBar, IconSettings,
  IconChevronDown, IconChevronRight
} from '@tabler/icons-react';
import {sidebarGroups, sidebarGroupForPath, isSidebarItemActive} from './sidebar-model.mjs';

const icons = {
  workbench:IconLayoutDashboard, organization:IconSitemap, api:IconCode,
  traffic:IconAdjustments, policy:IconShieldCheck, applications:IconBox,
  release:IconRocket, gateway:IconServer, observability:IconChartBar, governance:IconSettings
};

type Props = {
  path:string;
  expanded:string | null;
  onToggle:(id:string)=>void;
  onNavigate:(url:string)=>void;
};

export function SidebarNav({path, expanded, onToggle, onNavigate}:Props) {
  const activeGroup = sidebarGroupForPath(path);
  return <>{sidebarGroups.map(group => {
    const Icon = icons[group.id as keyof typeof icons];
    const open = expanded === group.id;
    const panelId = `sidebar-${group.id}`;
    return <section className="nav-section" key={group.id}>
      <button type="button" className={`nav-group${activeGroup === group.id ? ' active-group' : ''}`}
        disabled={group.unavailable}
        aria-expanded={group.unavailable ? undefined : open}
        aria-controls={group.unavailable ? undefined : panelId}
        onClick={() => onToggle(group.id)}>
        <Icon size={18} aria-hidden="true"/>
        <span className="nav-group-label">{group.title}</span>
        {group.unavailable ? <span className="nav-pending">待接入</span> : open ?
          <IconChevronDown size={15} aria-hidden="true"/> : <IconChevronRight size={15} aria-hidden="true"/>}
      </button>
      {!group.unavailable && <div id={panelId} hidden={!open} className="nav-children">
        {open && group.items.map(([url,title]) => <a key={url} href={url}
          className={isSidebarItemActive(path,url) ? 'active' : undefined}
          aria-current={isSidebarItemActive(path,url) ? 'page' : undefined}
          onClick={event => {
            // Keep browser link actions (new tab/window) available.
            if(event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            onNavigate(url);
          }}>{title}</a>)}
      </div>}
    </section>;
  })}</>;
}
