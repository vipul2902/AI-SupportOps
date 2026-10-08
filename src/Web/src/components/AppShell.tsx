import { useState } from 'react'
import { NavLink, Outlet } from 'react-router'
import { BarChart3, Bot, Building2, ChevronsUpDown, FileText, FlaskConical, LayoutDashboard, LogOut, Menu, MessagesSquare, Settings, Ticket, Users, X } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { hasRole, type TenantRole } from '../lib/types'
import { useToast } from './toast'
import { cx } from './ui'

interface NavItem { to: string; label: string; icon: typeof LayoutDashboard; minRole: TenantRole }

export const navItems: NavItem[] = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard, minRole: 'Viewer' },
  { to: '/chat', label: 'AI Assistant', icon: Bot, minRole: 'Viewer' },
  { to: '/conversations', label: 'Conversations', icon: MessagesSquare, minRole: 'Viewer' },
  { to: '/documents', label: 'Knowledge base', icon: FileText, minRole: 'Viewer' },
  { to: '/tickets', label: 'Tickets', icon: Ticket, minRole: 'Viewer' },
  { to: '/evaluations', label: 'Evaluations', icon: FlaskConical, minRole: 'Admin' },
  { to: '/team', label: 'Team', icon: Users, minRole: 'Viewer' },
  { to: '/settings', label: 'Settings', icon: Settings, minRole: 'Viewer' },
]

export function AppShell() {
  const { me } = useAuth()
  const [mobileOpen, setMobileOpen] = useState(false)

  return (
    <div className="flex h-full">
      <aside className={cx('fixed inset-y-0 left-0 z-40 w-64 border-r border-slate-200 bg-white transition-transform lg:static lg:translate-x-0',
        mobileOpen ? 'translate-x-0' : '-translate-x-full')}>
        <Sidebar onNavigate={() => setMobileOpen(false)} />
      </aside>
      {mobileOpen && <div className="fixed inset-0 z-30 bg-slate-900/30 lg:hidden" onClick={() => setMobileOpen(false)} aria-hidden />}

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 items-center gap-3 border-b border-slate-200 bg-white px-4 lg:hidden">
          <button onClick={() => setMobileOpen(o => !o)} aria-label="Toggle navigation" className="rounded p-1.5 text-slate-600 hover:bg-slate-100">
            {mobileOpen ? <X className="size-5" /> : <Menu className="size-5" />}
          </button>
          <span className="font-semibold">AI-SupportOps</span>
          <span className="ml-auto truncate text-sm text-slate-500">{me?.memberships.find(m => m.tenantId === me.tenantId)?.tenantName}</span>
        </header>
        <main className="min-h-0 flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

function Sidebar({ onNavigate }: { onNavigate: () => void }) {
  const { me, logout, switchTenant } = useAuth()
  const toast = useToast()
  const [switcherOpen, setSwitcherOpen] = useState(false)
  if (!me) return null
  const current = me.memberships.find(m => m.tenantId === me.tenantId)

  return (
    <div className="flex h-full flex-col">
      <div className="flex h-14 items-center gap-2 border-b border-slate-200 px-4">
        <div className="flex size-7 items-center justify-center rounded-lg bg-brand-600 text-white"><BarChart3 className="size-4" aria-hidden /></div>
        <span className="font-semibold tracking-tight">AI-SupportOps</span>
      </div>

      {/* Organization switcher: tokens are tenant-scoped, so switching issues a new session. */}
      <div className="relative border-b border-slate-200 p-3">
        <button onClick={() => setSwitcherOpen(o => !o)} aria-haspopup="listbox" aria-expanded={switcherOpen}
          className="flex w-full items-center gap-2 rounded-lg border border-slate-200 px-2.5 py-2 text-left text-sm hover:bg-slate-50">
          <Building2 className="size-4 text-slate-400" aria-hidden />
          <span className="min-w-0 flex-1">
            <span className="block truncate font-medium">{current?.tenantName}</span>
            <span className="block text-xs text-slate-500">{me.role}</span>
          </span>
          <ChevronsUpDown className="size-4 text-slate-400" aria-hidden />
        </button>
        {switcherOpen && (
          <ul role="listbox" className="absolute inset-x-3 top-full z-10 mt-1 rounded-lg border border-slate-200 bg-white py-1 shadow-lg">
            {me.memberships.map(m => (
              <li key={m.tenantId}>
                <button role="option" aria-selected={m.tenantId === me.tenantId} className="flex w-full items-center justify-between px-3 py-2 text-left text-sm hover:bg-slate-50"
                  onClick={async () => {
                    setSwitcherOpen(false)
                    if (m.tenantId === me.tenantId) return
                    try { await switchTenant(m.tenantId); toast.success(`Switched to ${m.tenantName}`) } catch (e) { toast.error(e) }
                  }}>
                  <span className="truncate">{m.tenantName}</span>
                  <span className="text-xs text-slate-400">{m.role}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <nav className="flex-1 space-y-0.5 overflow-y-auto p-3" aria-label="Main">
        {navItems.filter(item => hasRole(me.role, item.minRole)).map(item => (
          <NavLink key={item.to} to={item.to} onClick={onNavigate}
            className={({ isActive }) => cx('flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm font-medium transition',
              isActive ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900')}>
            <item.icon className="size-4" aria-hidden />
            {item.label}
          </NavLink>
        ))}
      </nav>

      <div className="flex items-center gap-2.5 border-t border-slate-200 p-3">
        <div className="flex size-8 items-center justify-center rounded-full bg-slate-200 text-xs font-semibold text-slate-700" aria-hidden>
          {me.displayName.split(' ').map(p => p[0]).slice(0, 2).join('').toUpperCase()}
        </div>
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium">{me.displayName}</p>
          <p className="truncate text-xs text-slate-500">{me.email}</p>
        </div>
        <button onClick={() => void logout()} aria-label="Sign out" className="rounded p-1.5 text-slate-500 hover:bg-slate-100 hover:text-slate-700">
          <LogOut className="size-4" />
        </button>
      </div>
    </div>
  )
}
