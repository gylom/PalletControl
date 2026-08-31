import React, { useEffect, useMemo, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

const API = '/api'
const BUILD_VERSION = '5.9.1'
const BUILD_CREDIT = 'Developed by Gytis Lukosevicius'

const THEME_OPTIONS = [
  { id: 'normal', name: 'Normal', group: 'standard', description: 'Original clean PalletControl theme.' },
  { id: 'dark', name: 'Dark', group: 'standard', description: 'Dark version of the normal PalletControl theme.' },
  { id: 'terminal', name: 'Terminal', group: 'special', description: 'Industrial terminal theme with warehouse and pallet styling.' },
  { id: 'pallet-stealer', name: 'Pallet Stealer', group: 'special', description: 'A darker playful pallet-themed look.' }
]

function normalizeTheme(theme) {
  return THEME_OPTIONS.some(t => t.id === theme) ? theme : 'normal'
}

function applyTheme(theme) {
  const next = normalizeTheme(theme)
  document.documentElement.dataset.theme = next
  localStorage.setItem('theme', next)
  return next
}

applyTheme(localStorage.getItem('theme') || 'normal')

console.info(`PalletControl frontend v${BUILD_VERSION} - terminal administration and persistent themes enabled`)

class ApiError extends Error {
  constructor(message, status, data) {
    super(message)
    this.status = status
    this.data = data
  }
}

function dateInput(d = new Date()) {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}


function makeIdempotencyKey() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID()
  return `${Date.now()}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`
}

function signed(v) {
  const n = Number(v || 0)
  return n > 0 ? `+${n}` : String(n)
}

function formatTimestamp(iso) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('nb-NO', {
    day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
  })
}

function formatDate(value) {
  if (!value) return '—'
  const [y, m, d] = String(value).slice(0, 10).split('-')
  return `${d}.${m}.${y}`
}

function periodDates(preset) {
  const now = new Date()
  const y = now.getFullYear()
  const m = now.getMonth()
  const today = dateInput(now)
  const mondayOffset = (now.getDay() + 6) % 7

  if (preset === 'today') return { from: today, to: today }
  if (preset === 'yesterday') {
    const yesterday = new Date(now)
    yesterday.setDate(now.getDate() - 1)
    const value = dateInput(yesterday)
    return { from: value, to: value }
  }
  if (preset === 'thisWeek') {
    const start = new Date(now)
    start.setDate(now.getDate() - mondayOffset)
    return { from: dateInput(start), to: today }
  }
  if (preset === 'previousWeek') {
    const thisMonday = new Date(now)
    thisMonday.setDate(now.getDate() - mondayOffset)
    const start = new Date(thisMonday)
    start.setDate(start.getDate() - 7)
    const end = new Date(thisMonday)
    end.setDate(end.getDate() - 1)
    return { from: dateInput(start), to: dateInput(end) }
  }
  if (preset === 'lastMonth') {
    return { from: dateInput(new Date(y, m - 1, 1)), to: dateInput(new Date(y, m, 0)) }
  }
  if (preset === 'thisYear') return { from: `${y}-01-01`, to: today }
  if (preset === 'lastYear') return { from: `${y - 1}-01-01`, to: `${y - 1}-12-31` }
  return { from: dateInput(new Date(y, m, 1)), to: today }
}

async function api(path, opts = {}) {
  const token = localStorage.getItem('token')
  const headers = { ...(opts.headers || {}) }
  if (!(opts.body instanceof FormData)) headers['Content-Type'] = 'application/json'
  if (token && path !== '/auth/login') headers.Authorization = `Bearer ${token}`

  const res = await fetch(API + path, { ...opts, headers })
  const text = await res.text()
  let data = null
  try { data = text ? JSON.parse(text) : null } catch { data = text }

  if (!res.ok) {
    const message = data?.message || data?.title || `Request failed (${res.status})`
    throw new ApiError(message, res.status, data)
  }
  return data
}

async function downloadApiFile(path, fallbackName) {
  const token = localStorage.getItem('token')
  const res = await fetch(API + path, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!res.ok) {
    let message = `Download failed (${res.status})`
    try { const data = await res.json(); message = data?.message || message } catch {}
    throw new Error(message)
  }
  const blob = await res.blob()
  const disposition = res.headers.get('content-disposition') || ''
  const match = disposition.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i)
  const name = match ? decodeURIComponent(match[1].replace(/"/g, '')) : fallbackName
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a'); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url)
}

function firstAllowedTab(me) {
  if (me?.role === 'Viewer' && me?.hasInternalPalletAccounting) return 'stats'
  if (me?.hasInternalPalletAccounting) return 'register'
  if (me?.hasLinehaul) return 'linehaulRegister'
  if (me?.hasReceivedControl) return 'receivedRegister'
  return 'settings'
}

function accountScopeLabel(me) {
  if (me?.role === 'Viewer') return me?.viewerScopeLabel || 'Assigned transporters'
  return me?.terminalCode || ''
}

function App() {
  const [me, setMe] = useState(() => {
    try {
      const cached = JSON.parse(localStorage.getItem('me'))
      if (cached?.token) {
        delete cached.token
        localStorage.setItem('me', JSON.stringify(cached))
      }
      return cached
    } catch { return null }
  })

  function logout() {
    localStorage.removeItem('token')
    localStorage.removeItem('me')
    setMe(null)
  }

  function updateSession(next) {
    const { token, ...session } = next || {}
    if (token) localStorage.setItem('token', token)
    localStorage.setItem('me', JSON.stringify(session))
    applyTheme(session?.theme || 'normal')
    setMe(session)
  }

  useEffect(() => {
    applyTheme(me?.theme || localStorage.getItem('theme') || 'normal')
  }, [me?.theme])

  // Keep role and terminal synchronized with the database while the user is logged in.
  // The backend also refreshes these values for authorization on every request, so this
  // is mainly for keeping the visible terminal badge/navigation current.
  useEffect(() => {
    if (!me) return

    let cancelled = false

    async function refreshMe() {
      try {
        const current = await api('/me')
        if (cancelled) return

        setMe(previous => {
          const merged = { ...previous, ...current }
          localStorage.setItem('me', JSON.stringify(merged))
          return merged
        })
      } catch (e) {
        if (!cancelled && e.status === 401) logout()
      }
    }

    refreshMe()
    const timer = setInterval(refreshMe, 15000)
    window.addEventListener('focus', refreshMe)

    return () => {
      cancelled = true
      clearInterval(timer)
      window.removeEventListener('focus', refreshMe)
    }
  }, [Boolean(me)])

  return <>
    {me ? <Shell me={me} logout={logout} onSessionUpdate={updateSession} /> : <Login onLogin={setMe} />}
    <BuildFooter />
  </>
}

function BuildFooter() {
  return <div className="buildFooter">PalletControl · Build {BUILD_VERSION} · {BUILD_CREDIT}</div>
}

function Login({ onLogin }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [forgotPassword, setForgotPassword] = useState(false)

  async function submit(e) {
    e.preventDefault()
    setBusy(true); setError('')
    try {
      const result = await api('/auth/login', {
        method: 'POST', body: JSON.stringify({ username, password })
      })
      const { token, ...session } = result
      localStorage.setItem('token', token)
      localStorage.setItem('me', JSON.stringify(session))
      applyTheme(session.theme || 'normal')
      onLogin(session)
    } catch (e) {
      setError(`Login failed: ${e.message}`)
    } finally { setBusy(false) }
  }

  if (forgotPassword) {
    return <div className="loginWrap">
      <div className="card login passwordHelp">
        <div className="brand">🔐</div>
        <h1>Password reset</h1>
        <p className="muted">Passwords are reset by the PalletControl administrator.</p>
        <div className="passwordHelpBox">
          <span>For password reset, contact</span>
          <a href="mailto:Gytis@transportservice.no?subject=PalletControl%20password%20reset">Gytis@transportservice.no</a>
        </div>
        <button type="button" className="primary big" onClick={() => { setForgotPassword(false); setError('') }}>Back to sign in</button>
      </div>
    </div>
  }

  return <div className="loginWrap">
    <form className="card login" onSubmit={submit}>
      <div className="brand">📦</div>
      <h1>Pallet Control</h1>
      <p className="muted">Sign in to your terminal</p>
      <label>Username<input value={username} onChange={e => setUsername(e.target.value)} /></label>
      <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} /></label>
      <button type="button" className="forgotPasswordLink" onClick={() => { setForgotPassword(true); setError('') }}>Forgot password?</button>
      {error && <div className="error">{error}</div>}
      <button className="primary big" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
    </form>
  </div>
}

function Shell({ me, logout, onSessionUpdate }) {
  const [tab, setTab] = useState(() => firstAllowedTab(me))
  const isSuperAdmin = me.role === 'SuperAdmin'
  const isTerminalAdmin = me.role === 'Admin' || me.role === 'TerminalAdmin'
  const isViewer = me.role === 'Viewer'
  const adminAccess = isSuperAdmin || isTerminalAdmin
  const elevated = adminAccess || me.role === 'Superuser'
  const canExportInternal = elevated || isViewer
  const hasInternal = me.hasInternalPalletAccounting === true
  const hasLinehaul = !isViewer && me.hasLinehaul === true
  const hasReceivedControl = !isViewer && me.hasReceivedControl === true
  const showDriverStatisticsTab = me.showDriverStatisticsTab !== false
  const showDailyCheckTab = me.showDailyCheckTab !== false
  const scopeLabel = accountScopeLabel(me)
  const terminalKey = `${me.terminalId}-${me.terminalCode}-${me.viewerScopeLabel || ''}`

  useEffect(() => {
    const internalTabs = ['register', 'stats', 'driverStats', 'dailyCheck', 'receipts', 'warnings', 'export']
    const linehaulTabs = ['linehaulRegister', 'linehaulReceipts', 'linehaulStats', 'linehaulExport', 'linehaulImport']
    const receivedTabs = ['receivedRegister', 'receivedStats', 'receivedWarnings', 'receivedExport', 'receivedImport']
    const fallback = firstAllowedTab(me)
    if (internalTabs.includes(tab) && !hasInternal) setTab(fallback)
    if (linehaulTabs.includes(tab) && !hasLinehaul) setTab(fallback)
    if (receivedTabs.includes(tab) && !hasReceivedControl) setTab(fallback)
    if (tab === 'admin' && !isSuperAdmin) setTab(fallback)
    if (tab === 'register' && isViewer) setTab('stats')
    if (tab === 'warnings' && !elevated) setTab(fallback)
    if (tab === 'export' && !canExportInternal) setTab(fallback)
    if (tab === 'settings' && isViewer) setTab('stats')
    if (tab === 'driverStats' && !showDriverStatisticsTab) setTab(fallback)
    if (tab === 'dailyCheck' && !showDailyCheckTab) setTab(fallback)
  }, [tab, me, hasInternal, hasLinehaul, hasReceivedControl, isSuperAdmin, isViewer, elevated, canExportInternal, showDriverStatisticsTab, showDailyCheckTab])

  return <div>
    <header>
      <div className="headerBrand"><b>📦 Pallet Control</b><span className="terminal">{scopeLabel}</span></div>
      <div className="userline">
        {isSuperAdmin && <SuperAdminTerminalSwitcher me={me} onSessionUpdate={onSessionUpdate} />}
        <span>{me.displayName} · {me.role}</span>
        <button className="linkbtn" onClick={logout}>Log out</button>
      </div>
    </header>

    <nav className="moduleNav">
      {hasInternal && <div className="navGroup navInternal">
        <span className="navGroupTitle">InternPalleregnskap</span>
        <div className="navGroupButtons">
          {!isViewer && <NavButton id="register" tab={tab} setTab={setTab}>Register</NavButton>}
          <NavButton id="stats" tab={tab} setTab={setTab}>Statistics</NavButton>
          {showDriverStatisticsTab && <NavButton id="driverStats" tab={tab} setTab={setTab}>Statistics Driver</NavButton>}
          {showDailyCheckTab && <NavButton id="dailyCheck" tab={tab} setTab={setTab}>Daily Check</NavButton>}
          <NavButton id="receipts" tab={tab} setTab={setTab}>Receipts</NavButton>
          {elevated && <NavButton id="warnings" tab={tab} setTab={setTab}>Warnings</NavButton>}
          {canExportInternal && <NavButton id="export" tab={tab} setTab={setTab}>Export</NavButton>}
        </div>
      </div>}

      {hasLinehaul && <div className="navGroup navLinehaul">
        <span className="navGroupTitle">Linehaul</span>
        <div className="navGroupButtons">
          <NavButton id="linehaulRegister" tab={tab} setTab={setTab}>Register</NavButton>
          <NavButton id="linehaulReceipts" tab={tab} setTab={setTab}>Receipts</NavButton>
          <NavButton id="linehaulStats" tab={tab} setTab={setTab}>Statistics</NavButton>
          <NavButton id="linehaulExport" tab={tab} setTab={setTab}>Export</NavButton>
          {adminAccess && <NavButton id="linehaulImport" tab={tab} setTab={setTab}>Import</NavButton>}
        </div>
      </div>}

      {hasReceivedControl && <div className="navGroup navReceived">
        <span className="navGroupTitle">MottattKontroll</span>
        <div className="navGroupButtons">
          <NavButton id="receivedRegister" tab={tab} setTab={setTab}>Register</NavButton>
          <NavButton id="receivedStats" tab={tab} setTab={setTab}>Statistics</NavButton>
          <NavButton id="receivedWarnings" tab={tab} setTab={setTab}>Warnings</NavButton>
          <NavButton id="receivedExport" tab={tab} setTab={setTab}>Export</NavButton>
          {adminAccess && <NavButton id="receivedImport" tab={tab} setTab={setTab}>Import</NavButton>}
        </div>
      </div>}

      {!isViewer && <div className="navGroup navUtility">
        <span className="navGroupTitle">Account</span>
        <div className="navGroupButtons">
          <NavButton id="settings" tab={tab} setTab={setTab}>Settings</NavButton>
          {isSuperAdmin && <NavButton id="admin" tab={tab} setTab={setTab}>Admin</NavButton>}
        </div>
      </div>}
    </nav>

    <div className="healthBarWrap"><HealthCheck /></div>

    <main>
      {tab === 'register' && hasInternal && !isViewer && <Register key={`register-${terminalKey}`} me={me} />}
      {tab === 'stats' && hasInternal && <Stats key={`stats-${terminalKey}`} me={me} />}
      {tab === 'driverStats' && hasInternal && showDriverStatisticsTab && <DriverStats key={`driver-stats-${terminalKey}`} me={me} />}
      {tab === 'dailyCheck' && hasInternal && showDailyCheckTab && <DailyVehicleCheck key={`daily-check-${terminalKey}`} me={me} />}
      {tab === 'receipts' && hasInternal && <Receipts key={`receipts-${terminalKey}`} me={me} />}
      {tab === 'warnings' && hasInternal && elevated && <Warnings key={`warnings-${terminalKey}`} me={me} />}
      {tab === 'export' && hasInternal && canExportInternal && <Export key={`export-${terminalKey}`} me={me} />}

      {tab === 'linehaulRegister' && hasLinehaul && <LinehaulRegister key={`lh-reg-${terminalKey}`} me={me} />}
      {tab === 'linehaulReceipts' && hasLinehaul && <LinehaulReceipts key={`lh-rec-${terminalKey}`} me={me} />}
      {tab === 'linehaulStats' && hasLinehaul && <LinehaulStats key={`lh-stats-${terminalKey}`} me={me} />}
      {tab === 'linehaulExport' && hasLinehaul && <LinehaulExport key={`lh-exp-${terminalKey}`} me={me} />}
      {tab === 'linehaulImport' && hasLinehaul && adminAccess && <LinehaulImport key={`lh-imp-${terminalKey}`} me={me} />}

      {tab === 'receivedRegister' && hasReceivedControl && <ReceivedControlRegister key={`rc-reg-${terminalKey}`} me={me} />}
      {tab === 'receivedStats' && hasReceivedControl && <ReceivedControlStats key={`rc-stats-${terminalKey}`} me={me} />}
      {tab === 'receivedWarnings' && hasReceivedControl && <ReceivedControlWarnings key={`rc-warn-${terminalKey}`} me={me} />}
      {tab === 'receivedExport' && hasReceivedControl && <ReceivedControlExport key={`rc-exp-${terminalKey}`} me={me} />}
      {tab === 'receivedImport' && hasReceivedControl && adminAccess && <ReceivedControlImport key={`rc-imp-${terminalKey}`} me={me} />}

      {tab === 'settings' && !isViewer && <UserSettings key={`settings-${terminalKey}`} me={me} />}
      {tab === 'admin' && isSuperAdmin && <Admin key={`admin-${terminalKey}`} me={me} />}
    </main>
  </div>
}

function NavButton({ id, tab, setTab, children }) {
  return <button className={tab === id ? 'active' : ''} onClick={() => setTab(id)}>{children}</button>
}

function SuperAdminTerminalSwitcher({ me, onSessionUpdate }) {
  const [terminals, setTerminals] = useState([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    api('/me/operating-terminals')
      .then(rows => { if (!cancelled) setTerminals(rows || []) })
      .catch(e => { if (!cancelled) setError(e.message) })
    return () => { cancelled = true }
  }, [])

  async function changeTerminal(e) {
    const terminalId = Number(e.target.value)
    if (!terminalId || terminalId === Number(me.terminalId)) return
    setBusy(true)
    setError('')
    try {
      const next = await api('/me/terminal', {
        method: 'POST',
        body: JSON.stringify({ terminalId })
      })
      onSessionUpdate(next)
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  return <div className="superAdminTerminalSwitch" title={error || 'Change active operating terminal'}>
    <span>Active</span>
    <select value={String(me.terminalId)} onChange={changeTerminal} disabled={busy || terminals.length === 0}>
      {terminals.length === 0 && <option value={String(me.terminalId)}>{me.terminalCode}</option>}
      {terminals.map(t => <option key={t.id} value={String(t.id)}>{t.code}</option>)}
    </select>
    {busy && <span className="terminalSwitchBusy">…</span>}
    {error && <span className="terminalSwitchError">!</span>}
  </div>
}

function HealthCheck() {
  const [health, setHealth] = useState({
    api: 'checking',
    database: 'checking',
    integrity: 'checking',
    overall: 'checking',
    checked: null
  })

  async function check() {
    try {
      const res = await fetch('/api/health', { cache: 'no-store' })
      const body = await res.json().catch(() => ({}))
      setHealth({
        api: 'online',
        database: body?.database?.status === 'online' ? 'online' : 'offline',
        integrity: body?.database?.quickCheck === 'ok' ? 'online' : 'offline',
        overall: res.ok && body?.status === 'healthy' ? 'healthy' : 'unhealthy',
        checked: new Date()
      })
    } catch {
      setHealth({
        api: 'offline',
        database: 'unknown',
        integrity: 'unknown',
        overall: 'unhealthy',
        checked: new Date()
      })
    }
  }

  useEffect(() => {
    check()
    const timer = setInterval(check, 60000)
    return () => clearInterval(timer)
  }, [])

  return <div
      className={`healthCheck ${health.overall}`}
      title="Real SQLite connection + PRAGMA quick_check. Detailed server information is available to SuperAdmin in System health."
  >
    <b>Health Check</b>
    <HealthDot label="API" value={health.api} />
    <HealthDot label="Database" value={health.database} />
    <HealthDot label="Integrity" value={health.integrity} />
    <span className="healthTime">{health.checked ? health.checked.toLocaleTimeString('nb-NO', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : 'checking…'}</span>
    <button className="tiny" onClick={check}>↻</button>
  </div>
}

function HealthDot({ label, value }) {
  return <span className="healthItem"><span className={`dot ${value}`}></span>{label}: {value}</span>
}

function Register({ me }) {
  const elevated = ['SuperAdmin', 'TerminalAdmin', 'Admin', 'Superuser'].includes(me.role)
  const [data, setData] = useState(null)
  const [vehicle, setVehicle] = useState('')
  const [driver, setDriver] = useState('')
  const [driverOptions, setDriverOptions] = useState([])
  const [direction, setDirection] = useState('')
  const [qty, setQty] = useState({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState(null)
  const [notifications, setNotifications] = useState([])
  const [businessDate, setBusinessDate] = useState(dateInput())

  async function load() {
    const result = await api('/setup/register')
    setData(result)
    setDriverOptions(result.drivers)
    return result
  }

  useEffect(() => { load().catch(e => setError(e.message)) }, [])

  async function changeVehicle(value) {
    setVehicle(value)
    setDriver('')
    setError('')
    if (!value) {
      setDriverOptions(data?.drivers || [])
      return
    }
    try {
      setDriverOptions(await api(`/drivers/for-vehicle/${value}`))
    } catch (e) {
      setDriverOptions(data?.drivers || [])
      setError(e.message)
    }
  }

  function validate() {
    if (!vehicle) return 'Choose a vehicle.'
    if (!driver) return 'Choose a driver.'
    if (!direction) return 'Choose PALLETS IN or PALLETS OUT.'
    if (elevated && !businessDate) return 'Choose a receipt date.'

    const enteredValues = Object.values(qty).filter(
        value => value !== '' && value !== null && value !== undefined
    )

    if (enteredValues.length === 0) return 'Enter a pallet quantity. 0 is allowed.'
    if (enteredValues.some(value => !Number.isFinite(Number(value)))) return 'Enter a valid pallet quantity.'
    if (enteredValues.some(value => Number(value) < 0)) return 'Pallet quantity cannot be negative.'
    return ''
  }

  async function submit() {
    const validationError = validate()
    if (validationError) {
      setError(validationError)
      return
    }

    const selectedVehicle = data.vehicles.find(x => Number(x.id) === Number(vehicle))
    const selectedDriver = driverOptions.find(x => Number(x.id) === Number(driver)) || data.drivers.find(x => Number(x.id) === Number(driver))
    const items = data.palletTypes
        .filter(p =>
            Object.prototype.hasOwnProperty.call(qty, p.id) &&
            qty[p.id] !== '' &&
            qty[p.id] !== null &&
            qty[p.id] !== undefined
        )
        .map(p => ({
          palletTypeId: Number(p.id),
          quantity: Number(qty[p.id])
        }))

    const summary = items
        .map(item => {
          const p = data.palletTypes.find(x => Number(x.id) === Number(item.palletTypeId))
          return `${p?.name || 'Pallet'}: ${item.quantity}`
        })
        .join('\n')

    const accepted = window.confirm(
        `Are you sure you want to submit?\n\n` +
        `Vehicle: ${selectedVehicle?.vehicleId || ''}\n` +
        `Transporter: ${selectedVehicle?.transporter || 'Not assigned'}\n` +
        `Driver: ${selectedDriver?.name || ''}\n` +
        `Direction: ${direction}\n` +
        `Receipt date: ${elevated ? businessDate : dateInput()}\n` +
        summary
    )
    if (!accepted) return

    const request = {
      idempotencyKey: makeIdempotencyKey(),
      vehicleId: Number(vehicle),
      driverId: Number(driver),
      direction,
      items,
      ...(elevated ? { businessDate } : {})
    }

    setBusy(true)
    setError('')
    setSuccess(null)
    setNotifications([])

    try {
      const check = await api('/receipts/check', {
        method: 'POST',
        body: JSON.stringify(request)
      })

      if (check.warnings?.length) {
        const warningText = check.warnings.map(w => `• ${w.message}`).join('\n')
        const submitAnyway = window.confirm(
            `⚠ Please check before submitting:\n\n${warningText}\n\nSubmit anyway?`
        )
        if (!submitAnyway) return
      }

      const result = await api('/receipts', {
        method: 'POST',
        body: JSON.stringify({ ...request, confirmWarnings: true })
      })

      const receipt = result.receipt || result
      setSuccess(receipt)
      setNotifications(result.notifications || [])
      setQty({})

      // Direction is intentionally cleared after every successful submission.
      // The next person must actively press PALLETS IN or PALLETS OUT.
      setDirection('')

      // Always return to Choose vehicle / Choose driver after a successful submission.
      setVehicle('')
      setDriver('')
      if (elevated) setBusinessDate(dateInput())

      const setup = await load()
      setDriverOptions(setup.drivers)
    } catch (e) {
      if (e.status === 409 && e.data?.warnings) {
        setError(e.data.warnings.map(x => x.message).join(' '))
      } else {
        setError(e.message)
      }
    } finally {
      setBusy(false)
    }
  }

  if (!data) return <div className="card">Loading…</div>

  return (
      <section>
        <h1>Register pallets</h1>

        {success && (
            <Modal
                title="✓ Receipt registered"
                close={() => {
                  setSuccess(null)
                  setNotifications([])
                }}
            >
              <div className="receiptSuccessSummary">
                <b>{success.receiptNumber}</b>
                <div>{success.transporter} · {success.vehicle}</div>
                <div>{success.driver}</div>
                <div>Receipt date: {success.businessDate}</div>
                <div>
                  {success.direction} · {success.items?.map(x => `${x.quantity} ${x.palletType}`).join(', ')}
                </div>
              </div>

              {notifications.length > 0 && (
                  <div className="notificationStack submitPopupNotices">
                    {notifications.map((n, i) => (
                        <div className="submitNotice" key={i}>{n}</div>
                    ))}
                  </div>
              )}

              <div className="modalActions">
                <button
                    type="button"
                    className="primary"
                    onClick={() => {
                      setSuccess(null)
                      setNotifications([])
                    }}
                >
                  Close
                </button>
              </div>
            </Modal>
        )}

        <div className="card formGrid">
          <label>
            Terminal
            <input value={me.terminalCode} disabled />
          </label>

          <label>
            Receipt date
            {elevated
                ? <input type="date" value={businessDate} onChange={e => setBusinessDate(e.target.value)} />
                : <input value={dateInput()} disabled />}
            {elevated && <span className="fieldHint">Manual dates are audit logged.</span>}
          </label>

          <label>
            Vehicle
            <select value={vehicle} onChange={e => changeVehicle(e.target.value)}>
              <option value="">Choose vehicle</option>
              {data.vehicles.map(v => (
                  <option key={v.id} value={v.id}>{v.vehicleId} — {v.transporter}</option>
              ))}
            </select>
          </label>

          <label>
            Driver
            <select value={driver} onChange={e => setDriver(e.target.value)}>
              <option value="">Choose driver</option>
              {driverOptions.map(d => (
                  <option key={d.id} value={d.id}>
                    {d.name}{d.usageCount ? ` (${d.usageCount} uses on this vehicle)` : ''}
                  </option>
              ))}
            </select>
          </label>

          <div className="direction">
            <button type="button" className={direction === 'IN' ? 'selected' : ''} onClick={() => setDirection('IN')}>PALLETS IN</button>
            <button type="button" className={direction === 'OUT' ? 'selected' : ''} onClick={() => setDirection('OUT')}>PALLETS OUT</button>
          </div>

          <div className="pallets">
            {data.palletTypes.map(p => (
                <div className="qtyRow" key={p.id}>
                  <span>{p.name}</span>
                  <button type="button" onClick={() => setQty(current => ({
                    ...current,
                    [p.id]: Math.max(0, Number(current[p.id] || 0) - 1)
                  }))}>−</button>
                  <input
                      inputMode="numeric"
                      type="number"
                      min="0"
                      max="10000"
                      value={qty[p.id] ?? ''}
                      onChange={e => setQty(current => ({ ...current, [p.id]: e.target.value }))}
                  />
                  <button type="button" onClick={() => setQty(current => ({
                    ...current,
                    [p.id]: Number(current[p.id] || 0) + 1
                  }))}>+</button>
                </div>
            ))}
          </div>

          {error && <div className="error">{error}</div>}

          <button type="button" className="primary submit" disabled={busy} onClick={submit}>
            {busy ? 'Submitting…' : 'SUBMIT RECEIPT'}
          </button>
        </div>
      </section>
  )
}

function Stats({ me }) {
  const initial = periodDates('thisMonth')
  const [options, setOptions] = useState({ transporters: [], vehicles: [], drivers: [], palletTypes: [] })
  const [preset, setPreset] = useState('thisMonth')
  const [from, setFrom] = useState(initial.from)
  const [to, setTo] = useState(initial.to)
  const [palletTypeId, setPalletTypeId] = useState('')
  const [transporterIds, setTransporterIds] = useState([])
  const [vehicleIds, setVehicleIds] = useState([])
  const [driverIds, setDriverIds] = useState([])
  const [sortBy, setSortBy] = useState('movementDesc')
  const [result, setResult] = useState(null)
  const [error, setError] = useState('')
  const [bestOpen, setBestOpen] = useState(false)
  const [bestPeriod, setBestPeriod] = useState('thisMonth')
  const [leaderboard, setLeaderboard] = useState(null)

  async function loadOptions() { setOptions(await api('/statistics/options')) }

  async function loadStats(nextFrom = from, nextTo = to) {
    const p = new URLSearchParams({ from: nextFrom, to: nextTo, sortBy })
    if (palletTypeId) p.set('palletTypeId', palletTypeId)
    if (transporterIds.length) p.set('transporterIds', transporterIds.join(','))
    if (vehicleIds.length) p.set('vehicleIds', vehicleIds.join(','))
    if (driverIds.length) p.set('driverIds', driverIds.join(','))
    setResult(await api(`/statistics?${p}`))
  }


  async function loadLeaderboard(period = bestPeriod) {
    const p = new URLSearchParams({ period })
    if (palletTypeId) p.set('palletTypeId', palletTypeId)
    setLeaderboard(await api(`/statistics/best-drivers?${p}`))
  }

  useEffect(() => {
    Promise.all([loadOptions(), loadStats()]).catch(e => setError(e.message))
  }, [])

  function changePreset(value) {
    setPreset(value)
    if (value !== 'custom') {
      const r = periodDates(value)
      setFrom(r.from); setTo(r.to)
    }
  }

  async function apply() {
    setError('')
    try {
      await loadStats()
      if (bestOpen) await loadLeaderboard()
    } catch (e) { setError(e.message) }
  }

  async function applyQuickPeriod(value) {
    const r = periodDates(value)
    setPreset(value)
    setFrom(r.from)
    setTo(r.to)
    setError('')
    try {
      await loadStats(r.from, r.to)
    } catch (e) { setError(e.message) }
  }

  async function toggleBest() {
    const next = !bestOpen
    setBestOpen(next)
    if (next) {
      try { await loadLeaderboard(bestPeriod) } catch (e) { setError(e.message) }
    }
  }

  async function selectBestPeriod(period) {
    setBestPeriod(period)
    try { await loadLeaderboard(period) } catch (e) { setError(e.message) }
  }

  const visibleVehicles = useMemo(() => {
    if (!transporterIds.length) return options.vehicles
    const set = new Set(transporterIds.map(Number))
    return options.vehicles.filter(v => v.transporterId && set.has(Number(v.transporterId)))
  }, [options.vehicles, transporterIds])

  return <section>
    <div className="pageTitle"><div><h1>Statistics · {accountScopeLabel(me)}</h1><p>{me.role === 'Viewer' ? `Only pallet movements for your assigned transporter(s) are shown: ${accountScopeLabel(me)}.` : `Only pallet movements belonging to terminal ${me.terminalCode} are shown.`}</p></div>
      <button className="trophy" onClick={toggleBest}>🏆 Best Performing Driver</button>
    </div>
    {error && <div className="error">{error}</div>}

    {bestOpen && <div className="card leaderboardCard">
      <div className="sectionHead"><div><h2>🏆 Best Performing Drivers</h2><p>Ranked by highest positive balance.</p></div>
        <div className="segmented">
          {['thisWeek', 'thisMonth', 'lastMonth'].map(p => <button key={p} className={bestPeriod === p ? 'active' : ''} onClick={() => selectBestPeriod(p)}>
            {p === 'thisWeek' ? 'This week' : p === 'thisMonth' ? 'This month' : 'Last month'}
          </button>)}
        </div>
      </div>
      {leaderboard && <>
        <div className="muted small">{formatDate(leaderboard.from)} → {formatDate(leaderboard.to)}{palletTypeId ? ' · current pallet type filter' : ' · all pallet types'}</div>
        <div className="leaderboard">
          {leaderboard.drivers.length === 0 && <Empty text="No driver activity in this period." />}
          {leaderboard.drivers.map(r => <div className={`leaderRow rank${r.rank}`} key={r.driverId}>
            <div className="rank">{r.rank === 1 ? '🥇' : r.rank === 2 ? '🥈' : r.rank === 3 ? '🥉' : `#${r.rank}`}</div>
            <div className="leaderName"><b>{r.driver}</b><span>{r.vehicle}</span></div>
            <div className="leaderNumbers"><span>IN {r.inQty}</span><span>OUT {r.outQty}</span><strong className={r.balance >= 0 ? 'positive' : 'negative'}>{signed(r.balance)}</strong></div>
          </div>)}
        </div>
      </>}
    </div>}

    <div className="card statsFilterCard">
      <div className="segmented" style={{ marginBottom: 14, flexWrap: 'wrap' }}>
        {[
          ['thisWeek', 'This week'],
          ['previousWeek', 'Last week'],
          ['thisMonth', 'This month'],
          ['lastMonth', 'Last month'],
          ['thisYear', 'This year'],
          ['lastYear', 'Last year']
        ].map(([value, label]) => <button key={value} className={preset === value ? 'active' : ''} onClick={() => applyQuickPeriod(value)}>{label}</button>)}
      </div>
      <div className="filterGrid">
        <label>Date period<select value={preset} onChange={e => changePreset(e.target.value)}>
          <option value="thisWeek">This week</option><option value="previousWeek">Previous week</option>
          <option value="thisMonth">This month</option><option value="lastMonth">Last month</option>
          <option value="thisYear">This year</option><option value="lastYear">Last year</option><option value="custom">Custom dates</option>
        </select></label>
        <label>From<input type="date" value={from} onChange={e => { setPreset('custom'); setFrom(e.target.value) }} /></label>
        <label>To<input type="date" value={to} onChange={e => { setPreset('custom'); setTo(e.target.value) }} /></label>
        <label>Pallet type<select value={palletTypeId} onChange={e => setPalletTypeId(e.target.value)}>
          <option value="">All pallet types</option>{options.palletTypes.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select></label>
        <MultiSelect label="Transporter" options={options.transporters} selected={transporterIds} setSelected={ids => { setTransporterIds(ids); if (ids.length) { const allowed = new Set(options.vehicles.filter(v => ids.map(Number).includes(Number(v.transporterId))).map(v => Number(v.id))); setVehicleIds(old => old.filter(id => allowed.has(Number(id)))) } }} labelKey={me.role === 'Viewer' ? 'label' : 'name'} />
        <MultiSelect label="Vehicle" options={visibleVehicles.map(v => ({ ...v, name: `${v.vehicleId} — ${v.transporter}` }))} selected={vehicleIds} setSelected={setVehicleIds} labelKey="name" />
        <MultiSelect label="Driver name" options={options.drivers} selected={driverIds} setSelected={setDriverIds} labelKey="name" />
        <label>Sort<select value={sortBy} onChange={e => setSortBy(e.target.value)}>
          <option value="movementDesc">Highest total movement</option><option value="inDesc">Highest IN</option>
          <option value="outDesc">Highest OUT</option><option value="balanceDesc">Highest balance</option>
          <option value="balanceAsc">Lowest balance</option><option value="vehicleAsc">Vehicle A-Z</option>
        </select></label>
      </div>
      <button className="primary" onClick={apply}>Apply filters</button>
    </div>

    {result && <>
      <div className="statsCards">
        <StatCard label="IN" value={result.totalIn} /><StatCard label="OUT" value={result.totalOut} />
        <StatCard label="BALANCE" value={signed(result.totalBalance)} cls={result.totalBalance >= 0 ? 'positive' : 'negative'} />
      </div>
      <div className="tableWrap"><table><thead><tr><th>Transporter</th><th>Vehicle</th><th>Pallet type</th><th>IN</th><th>OUT</th><th>Balance</th><th>Movement</th></tr></thead>
        <tbody>{result.rows.map((r, i) => <tr key={`${r.vehicle}-${r.palletType}-${i}`}><td>{r.transporter}</td><td><b>{r.vehicle}</b></td><td>{r.palletType}</td><td>{r.inQty}</td><td>{r.outQty}</td><td className={r.balance >= 0 ? 'positive' : 'negative'}><b>{signed(r.balance)}</b></td><td>{r.movement}</td></tr>)}</tbody></table></div>
      {result.totalsByPalletType?.length > 0 && <div className="card totalsCard"><h3>Totals by pallet type</h3>{result.totalsByPalletType.map(t => <div className="totalLine" key={t.palletType}><b>{t.palletType}</b><span>IN {t.inQty}</span><span>OUT {t.outQty}</span><strong className={t.balance >= 0 ? 'positive' : 'negative'}>{signed(t.balance)}</strong></div>)}</div>}
    </>}
  </section>
}

function DriverStats({ me }) {
  const initial = periodDates('thisMonth')
  const [options, setOptions] = useState({ transporters: [], vehicles: [], drivers: [], palletTypes: [] })
  const [preset, setPreset] = useState('thisMonth')
  const [from, setFrom] = useState(initial.from)
  const [to, setTo] = useState(initial.to)
  const [palletTypeId, setPalletTypeId] = useState('')
  const [transporterIds, setTransporterIds] = useState([])
  const [vehicleIds, setVehicleIds] = useState([])
  const [driverIds, setDriverIds] = useState([])
  const [sortBy, setSortBy] = useState('movementDesc')
  const [viewMode, setViewMode] = useState('all')
  const [result, setResult] = useState(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function loadOptions() { setOptions(await api('/statistics/options')) }

  async function load(nextFrom = from, nextTo = to) {
    const p = new URLSearchParams({ from: nextFrom, to: nextTo, sortBy })
    if (palletTypeId) p.set('palletTypeId', palletTypeId)
    if (transporterIds.length) p.set('transporterIds', transporterIds.join(','))
    if (vehicleIds.length) p.set('vehicleIds', vehicleIds.join(','))
    if (driverIds.length) p.set('driverIds', driverIds.join(','))
    setResult(await api(`/statistics/drivers?${p}`))
  }

  useEffect(() => {
    Promise.all([loadOptions(), load()]).catch(e => setError(e.message))
  }, [])

  function changePreset(value) {
    setPreset(value)
    if (value !== 'custom') {
      const r = periodDates(value)
      setFrom(r.from)
      setTo(r.to)
    }
  }

  async function apply(nextFrom = from, nextTo = to) {
    setError('')
    setBusy(true)
    try { await load(nextFrom, nextTo) }
    catch (e) { setError(e.message) }
    finally { setBusy(false) }
  }

  async function applyQuickPeriod(value) {
    const r = periodDates(value)
    setPreset(value)
    setFrom(r.from)
    setTo(r.to)
    await apply(r.from, r.to)
  }

  const visibleVehicles = useMemo(() => {
    if (!transporterIds.length) return options.vehicles
    const set = new Set(transporterIds.map(Number))
    return options.vehicles.filter(v => v.transporterId && set.has(Number(v.transporterId)))
  }, [options.vehicles, transporterIds])

  return <section>
    <div className="pageTitle">
      <div>
        <h1>Statistics Driver · {accountScopeLabel(me)}</h1>
        <p>{me.role === 'Viewer' ? `Driver statistics are restricted to your assigned transporter(s): ${accountScopeLabel(me)}. ` : `Full driver statistics for terminal ${me.terminalCode}. `}Adjusted mode deducts the configured amount for every unmatched IN receipt. Cancelled receipts are ignored.</p>
      </div>
    </div>

    {error && <div className="error">{error}</div>}

    <div className="card statsFilterCard">
      <div className="sectionHead">
        <div className="segmented" style={{ flexWrap: 'wrap' }}>
          {[
            ['today', 'Today'], ['yesterday', 'Yesterday'], ['thisWeek', 'This week'], ['previousWeek', 'Last week'],
            ['thisMonth', 'This month'], ['lastMonth', 'Last month'], ['thisYear', 'This year'], ['lastYear', 'Last year']
          ].map(([value, label]) => <button key={value} className={preset === value ? 'active' : ''} onClick={() => applyQuickPeriod(value)} disabled={busy}>{label}</button>)}
        </div>
        <div className="segmented">
          <button className={viewMode === 'all' ? 'active' : ''} onClick={() => setViewMode('all')}>All / raw</button>
          <button className={viewMode === 'adjusted' ? 'active' : ''} onClick={() => setViewMode('adjusted')}>Adjusted</button>
        </div>
      </div>

      <div className="filterGrid">
        <label>Date period<select value={preset} onChange={e => changePreset(e.target.value)}>
          <option value="today">Today</option><option value="yesterday">Yesterday</option>
          <option value="thisWeek">This week</option><option value="previousWeek">Previous week</option>
          <option value="thisMonth">This month</option><option value="lastMonth">Last month</option>
          <option value="thisYear">This year</option><option value="lastYear">Last year</option><option value="custom">Custom dates</option>
        </select></label>
        <label>From<input type="date" value={from} onChange={e => { setPreset('custom'); setFrom(e.target.value) }} /></label>
        <label>To<input type="date" value={to} onChange={e => { setPreset('custom'); setTo(e.target.value) }} /></label>
        <label>Pallet type<select value={palletTypeId} onChange={e => setPalletTypeId(e.target.value)}><option value="">All pallet types</option>{options.palletTypes.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        <MultiSelect label="Transporter" options={options.transporters} selected={transporterIds} setSelected={ids => {
          setTransporterIds(ids)
          if (ids.length) {
            const allowed = new Set(options.vehicles.filter(v => ids.map(Number).includes(Number(v.transporterId))).map(v => Number(v.id)))
            setVehicleIds(old => old.filter(id => allowed.has(Number(id))))
          }
        }} labelKey={me.role === 'Viewer' ? 'label' : 'name'} />
        <MultiSelect label="Vehicle" options={visibleVehicles.map(v => ({ ...v, name: `${v.vehicleId} — ${v.transporter}` }))} selected={vehicleIds} setSelected={setVehicleIds} labelKey="name" />
        <MultiSelect label="Driver name" options={options.drivers} selected={driverIds} setSelected={setDriverIds} labelKey="name" />
        <label>Sort<select value={sortBy} onChange={e => setSortBy(e.target.value)}>
          <option value="movementDesc">Highest movement</option><option value="inDesc">Highest IN</option><option value="outDesc">Highest OUT</option>
          <option value="rawBalanceDesc">Highest raw balance</option><option value="adjustedBalanceDesc">Highest adjusted balance</option>
          <option value="unmatchedDesc">Most unmatched IN</option><option value="driverAsc">Driver A-Z</option>
        </select></label>
      </div>
      <button className="primary" onClick={() => apply()} disabled={busy}>{busy ? 'Loading…' : 'Apply filters'}</button>
    </div>

    {result && <>
      <div className="card" style={{ marginTop: 16 }}>
        <div className="sectionHead">
          <div>
            <h2>{viewMode === 'adjusted' ? 'Adjusted driver statistics' : 'All receipts / raw statistics'}</h2>
            <p>{viewMode === 'adjusted'
                ? `Every unmatched IN receipt deducts ${result.deductionPerUnmatchedIn} pallets. Matching is per driver + day, regardless of vehicle.`
                : 'Raw mode shows the actual pallet quantities without any pairing deduction.'}</p>
          </div>
        </div>
      </div>

      <div className="statsCards">
        <StatCard label="IN" value={result.totalIn} />
        <StatCard label="OUT" value={result.totalOut} />
        <StatCard label="RAW BALANCE" value={signed(result.totalRawBalance)} cls={result.totalRawBalance >= 0 ? 'positive' : 'negative'} />
        {viewMode === 'adjusted' && <StatCard label="UNMATCHED IN" value={result.totalUnmatchedInReceipts} cls={result.totalUnmatchedInReceipts > 0 ? 'negative' : 'positive'} />}
        {viewMode === 'adjusted' && <StatCard label="DEDUCTION" value={result.totalDeduction ? `-${result.totalDeduction}` : '0'} cls={result.totalDeduction > 0 ? 'negative' : ''} />}
        {viewMode === 'adjusted' && <StatCard label="ADJUSTED BALANCE" value={signed(result.totalAdjustedBalance)} cls={result.totalAdjustedBalance >= 0 ? 'positive' : 'negative'} />}
      </div>

      <div className="tableWrap"><table><thead><tr>
        <th>Driver</th><th>Vehicle(s)</th><th>IN receipts</th><th>OUT receipts</th>
        {viewMode === 'adjusted' && <th>Unmatched IN</th>}
        <th>IN pallets</th><th>OUT pallets</th><th>Raw balance</th>
        {viewMode === 'adjusted' && <><th>Deduction</th><th>Adjusted balance</th></>}
        <th>Movement</th>
      </tr></thead><tbody>
      {result.rows.map(r => <tr key={`${r.driverId}-${r.driver}`}>
        <td><b>{r.driver}</b></td><td>{r.vehicles || '—'}</td><td>{r.inReceipts}</td><td>{r.outReceipts}</td>
        {viewMode === 'adjusted' && <td className={r.unmatchedInReceipts > 0 ? 'negative' : ''}><b>{r.unmatchedInReceipts}</b></td>}
        <td>{r.inQty}</td><td>{r.outQty}</td><td className={r.rawBalance >= 0 ? 'positive' : 'negative'}><b>{signed(r.rawBalance)}</b></td>
        {viewMode === 'adjusted' && <><td className={r.deduction > 0 ? 'negative' : ''}>{r.deduction ? `-${r.deduction}` : '0'}</td><td className={r.adjustedBalance >= 0 ? 'positive' : 'negative'}><b>{signed(r.adjustedBalance)}</b></td></>}
        <td>{r.movement}</td>
      </tr>)}
      </tbody></table></div>
      {result.rows.length === 0 && <Empty text="No drivers match these filters." />}

      {viewMode === 'adjusted' && result.adjustmentDetails?.length > 0 && <div className="card" style={{ marginTop: 16 }}>
        <div className="sectionHead"><div><h2>Adjustment details</h2><p>These are the exact driver + day combinations where IN receipts were not fully matched by OUT receipts. OUT receipts can match IN receipts across different vehicles used by the same driver that day.</p></div></div>
        <div className="tableWrap"><table><thead><tr><th>Date</th><th>Driver</th><th>Vehicles</th><th>IN receipts</th><th>OUT receipts</th><th>Unmatched IN</th><th>Deduction</th></tr></thead><tbody>
        {result.adjustmentDetails.map((r, i) => <tr key={`${r.driverId}-${r.vehicle}-${r.date}-${i}`}>
          <td>{formatDate(r.date)}</td><td><b>{r.driver}</b></td><td>{r.vehicle}</td><td>{r.inReceipts}</td><td>{r.outReceipts}</td><td className="negative"><b>{r.unmatchedInReceipts}</b></td><td className="negative"><b>-{r.deduction}</b></td>
        </tr>)}
        </tbody></table></div>
      </div>}
    </>}
  </section>
}

function DailyVehicleCheck({ me }) {
  const initial = periodDates('thisMonth')
  const [options, setOptions] = useState({ transporters: [], vehicles: [], drivers: [], palletTypes: [] })
  const [preset, setPreset] = useState('thisMonth')
  const [from, setFrom] = useState(initial.from)
  const [to, setTo] = useState(initial.to)
  const [vehicleIds, setVehicleIds] = useState([])
  const [driverIds, setDriverIds] = useState([])
  const [compliance, setCompliance] = useState(null)
  const [showCompleteCompliance, setShowCompleteCompliance] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function loadOptions() { setOptions(await api('/statistics/options')) }

  async function load(nextFrom = from, nextTo = to) {
    const p = new URLSearchParams({ from: nextFrom, to: nextTo })
    if (vehicleIds.length) p.set('vehicleIds', vehicleIds.join(','))
    if (driverIds.length) p.set('driverIds', driverIds.join(','))
    setCompliance(await api(`/compliance?${p}`))
  }

  useEffect(() => {
    Promise.all([loadOptions(), load()]).catch(e => setError(e.message))
  }, [])

  function changePreset(value) {
    setPreset(value)
    if (value !== 'custom') {
      const r = periodDates(value)
      setFrom(r.from)
      setTo(r.to)
    }
  }

  async function apply(nextFrom = from, nextTo = to) {
    setError('')
    setBusy(true)
    try { await load(nextFrom, nextTo) }
    catch (e) { setError(e.message) }
    finally { setBusy(false) }
  }

  async function applyQuickPeriod(value) {
    const r = periodDates(value)
    setPreset(value)
    setFrom(r.from)
    setTo(r.to)
    await apply(r.from, r.to)
  }

  return <section>
    <div className="pageTitle">
      <div>
        <h1>Daily vehicle receipt check · {accountScopeLabel(me)}</h1>
        <p>Checks whether each scheduled vehicle has at least one IN and one OUT receipt on its expected operating days. Holidays and unscheduled days are excluded from the requirement, but receipts can still be submitted on those days.</p>
      </div>
    </div>

    {error && <div className="error">{error}</div>}

    <div className="card statsFilterCard">
      <div className="segmented" style={{ marginBottom: 14, flexWrap: 'wrap' }}>
        {[
          ['today', 'Today'], ['yesterday', 'Yesterday'], ['thisWeek', 'This week'], ['previousWeek', 'Last week'],
          ['thisMonth', 'This month'], ['lastMonth', 'Last month'], ['thisYear', 'This year'], ['lastYear', 'Last year']
        ].map(([value, label]) => <button key={value} className={preset === value ? 'active' : ''} onClick={() => applyQuickPeriod(value)} disabled={busy}>{label}</button>)}
      </div>

      <div className="filterGrid">
        <label>Date period<select value={preset} onChange={e => changePreset(e.target.value)}>
          <option value="today">Today</option><option value="yesterday">Yesterday</option>
          <option value="thisWeek">This week</option><option value="previousWeek">Previous week</option>
          <option value="thisMonth">This month</option><option value="lastMonth">Last month</option>
          <option value="thisYear">This year</option><option value="lastYear">Last year</option><option value="custom">Custom dates</option>
        </select></label>
        <label>From<input type="date" value={from} onChange={e => { setPreset('custom'); setFrom(e.target.value) }} /></label>
        <label>To<input type="date" value={to} onChange={e => { setPreset('custom'); setTo(e.target.value) }} /></label>
        <MultiSelect label="Vehicle" options={options.vehicles.map(v => ({ ...v, name: `${v.vehicleId} — ${v.transporter}` }))} selected={vehicleIds} setSelected={setVehicleIds} labelKey="name" />
        <MultiSelect label="Driver name" options={options.drivers} selected={driverIds} setSelected={setDriverIds} labelKey="name" />
      </div>

      {driverIds.length > 0 && <div className="muted small" style={{ marginBottom: 12 }}>Driver filtering shows scheduled vehicle-days where the selected driver submitted at least one IN or OUT receipt. A day with no receipts cannot be assigned to a driver.</div>}
      <button className="primary" onClick={() => apply()} disabled={busy}>{busy ? 'Loading…' : 'Apply filters'}</button>
    </div>

    {compliance && <>
      <div className="statsCards">
        <StatCard label="EXPECTED VEHICLE-DAYS" value={compliance.expectedVehicleDays} />
        <StatCard label="COMPLETE" value={compliance.completeVehicleDays} cls="positive" />
        <StatCard label="MISSED (PAST DAYS)" value={compliance.missedVehicleDays} cls={compliance.missedVehicleDays > 0 ? 'negative' : 'positive'} />
        <StatCard label="PENDING TODAY" value={compliance.pendingTodayVehicleDays} />
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div className="sectionHead">
          <div>
            <h2>Vehicle-day status</h2>
            <p>Schedule controls when a receipt is expected; it never blocks a vehicle from submitting on another day. Cancelled receipts do not count as IN or OUT.</p>
          </div>
          <label className="miniCheck"><input type="checkbox" checked={showCompleteCompliance} onChange={e => setShowCompleteCompliance(e.target.checked)} /> Show completed</label>
        </div>

        {compliance.holidays?.length > 0 && <div className="muted small" style={{ marginBottom: 10 }}>Excluded holidays: {compliance.holidays.map(h => `${formatDate(h.date)} ${h.name}`).join(' · ')}</div>}

        <div className="tableWrap"><table><thead><tr><th>Date</th><th>Vehicle</th><th>Transporter</th><th>IN receipt</th><th>IN driver(s)</th><th>OUT receipt</th><th>OUT driver(s)</th><th>Status</th></tr></thead>
          <tbody>{compliance.rows
              .filter(r => showCompleteCompliance || !r.complete)
              .map(r => <tr key={`${r.date}-${r.vehicleId}`}>
                <td>{formatDate(r.date)}{r.isToday ? ' · today' : ''}</td>
                <td><b>{r.vehicle}</b></td>
                <td>{r.transporter}</td>
                <td className={r.hasIn ? 'positive' : 'negative'}><b>{r.hasIn ? 'YES' : 'MISSING'}</b></td>
                <td>{r.inDrivers?.length ? r.inDrivers.join(', ') : '—'}</td>
                <td className={r.hasOut ? 'positive' : 'negative'}><b>{r.hasOut ? 'YES' : 'MISSING'}</b></td>
                <td>{r.outDrivers?.length ? r.outDrivers.join(', ') : '—'}</td>
                <td className={r.complete ? 'positive' : (r.isToday ? '' : 'negative')}><b>{r.complete ? 'Complete' : r.isToday ? 'Pending today' : r.status === 'MISSING_IN' ? 'Missing IN' : r.status === 'MISSING_OUT' ? 'Missing OUT' : 'Missing IN + OUT'}</b></td>
              </tr>)}
          </tbody></table></div>

        {!showCompleteCompliance && compliance.rows.filter(r => !r.complete).length === 0 && <Empty text="All expected vehicle-days in this filtered period have both an IN and an OUT receipt." />}
      </div>
    </>}
  </section>
}

function MultiSelect({
                       label,
                       options,
                       selected,
                       setSelected,
                       valueKey = 'id',
                       labelKey = 'name'
                     }) {
  const selectedSet = useMemo(() => new Set(selected.map(Number)), [selected])
  const summary = selected.length === 0
      ? 'All'
      : selected.length === 1
          ? options.find(x => Number(x[valueKey]) === Number(selected[0]))?.[labelKey] || '1 selected'
          : `${selected.length} selected`

  function toggle(id) {
    const numericId = Number(id)
    if (selectedSet.has(numericId)) {
      setSelected(selected.filter(x => Number(x) !== numericId))
    } else {
      setSelected([...selected, numericId])
    }
  }

  return (
      <details className="multiSelect">
        <summary><span>{label}</span><b>{summary}</b></summary>
        <div className="multiMenu">
          <button type="button" className="smallLink" onClick={() => setSelected([])}>Show all</button>
          {options.length === 0 && <div className="muted smallText">No choices available.</div>}
          {options.map(option => {
            const id = Number(option[valueKey])
            return (
                <label className="checkChoice" key={id}>
                  <input type="checkbox" checked={selectedSet.has(id)} onChange={() => toggle(id)} />
                  <span>{option[labelKey]}</span>
                </label>
            )
          })}
        </div>
      </details>
  )
}

function StatCard({ label, value, cls = '' }) { return <div className="statCard"><span>{label}</span><strong className={cls}>{value}</strong></div> }

function Receipts({ me }) {
  const canManage = ['SuperAdmin', 'TerminalAdmin', 'Admin', 'Superuser'].includes(me.role)
  const canFilter = canManage || me.role === 'Viewer'
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [limit, setLimit] = useState('10')
  const [sort, setSort] = useState('desc')
  const [statusFilter, setStatusFilter] = useState('all')
  const [search, setSearch] = useState('')
  const [rows, setRows] = useState([])
  const [error, setError] = useState('')
  const [detail, setDetail] = useState(null)

  async function load(next = {}) {
    const values = {
      from: next.from ?? from,
      to: next.to ?? to,
      limit: next.limit ?? limit,
      sort: next.sort ?? sort,
      status: next.status ?? statusFilter,
      search: next.search ?? search
    }
    const p = new URLSearchParams()

    if (canFilter) {
      if (values.from) p.set('from', values.from)
      if (values.to) p.set('to', values.to)
      p.set('sort', values.sort)
      p.set('limit', values.limit === 'all' ? '0' : values.limit)
      p.set('status', values.status)
      if (values.search.trim()) p.set('search', values.search.trim())
    } else {
      p.set('limit', '10')
      p.set('sort', 'desc')
    }

    const result = await api(`/receipts?${p}`)
    setRows(result.receipts || [])
  }

  useEffect(() => { load().catch(e => setError(e.message)) }, [])

  async function apply(next) {
    setError('')
    try { await load(next) }
    catch (e) { setError(e.message) }
  }

  async function changeFrom(value) {
    setFrom(value)
    if (value && to && value > to) {
      setTo(value)
      await apply({ from: value, to: value })
      return
    }
    await apply({ from: value })
  }

  async function changeTo(value) {
    setTo(value)
    if (value && from && value < from) {
      setFrom(value)
      await apply({ from: value, to: value })
      return
    }
    await apply({ to: value })
  }

  async function clearDates() {
    setFrom(''); setTo('')
    await apply({ from: '', to: '' })
  }

  async function todayOnly() {
    const today = dateInput()
    setFrom(today); setTo(today)
    await apply({ from: today, to: today })
  }

  async function changeLimit(value) { setLimit(value); await apply({ limit: value }) }
  async function changeSort(value) { setSort(value); await apply({ sort: value }) }
  async function changeStatus(value) { setStatusFilter(value); await apply({ status: value }) }

  async function submitSearch(e) {
    e.preventDefault()
    await apply({ search })
  }

  async function clearSearch() {
    setSearch('')
    await apply({ search: '' })
  }

  async function cancel(r) {
    if (!canManage) return
    const reason = window.prompt(`Why cancel ${r.receiptNumber}?`)
    if (!reason?.trim()) return
    try { await api(`/receipts/${r.id}/cancel`, { method: 'POST', body: JSON.stringify({ reason }) }); await load(); setDetail(null) }
    catch (e) { setError(e.message) }
  }

  async function reverse(r) {
    if (!canManage) return
    const reason = window.prompt(`Reason for reversing cancellation of ${r.receiptNumber}?`, 'Cancellation reversed')
    if (reason === null) return
    try { await api(`/receipts/${r.id}/reverse-cancellation`, { method: 'POST', body: JSON.stringify({ reason }) }); await load(); setDetail(null) }
    catch (e) { setError(e.message) }
  }

  const scope = accountScopeLabel(me)
  const filterDescription = me.role === 'Viewer'
      ? `Only receipts for your assigned transporter(s) are shown: ${scope}. Latest 10 are preloaded.`
      : `Latest 10 receipts for ${me.terminalCode} are preloaded. Use From/To to load a date range.`

  return <section>
    <div className="pageTitle"><div><h1>Receipts · {scope}</h1><p>{canFilter ? filterDescription : `Latest 10 receipts for ${me.terminalCode}.`}</p></div></div>
    {error && <div className="error">{error}</div>}

    {canFilter && <div className="card receiptControls receiptRangeControls">
      <label>From<input type="date" value={from} onChange={e => changeFrom(e.target.value)} /></label>
      <label>To<input type="date" value={to} onChange={e => changeTo(e.target.value)} /></label>
      <div className="receiptDateActions">
        <button type="button" onClick={todayOnly}>Today</button>
        {(from || to) && <button type="button" className="smallLink" onClick={clearDates}>Clear dates</button>}
      </div>
      <label>Time order<select value={sort} onChange={e => changeSort(e.target.value)}><option value="desc">Newest first</option><option value="asc">Oldest first</option></select></label>
      <div className="receiptStatusFilters"><span>Status:</span>{[
        ['all', 'All'], ['active', 'Active'], ['cancelled', 'Cancelled'], ['reversed', 'Reversed']
      ].map(([value, label]) => <button key={value} className={statusFilter === value ? 'active' : ''} onClick={() => changeStatus(value)}>{label}</button>)}</div>
      <div className="limitButtons"><span>Show:</span>{['10', '25', '50', 'all'].map(v => <button key={v} className={limit === v ? 'active' : ''} onClick={() => changeLimit(v)}>{v === 'all' ? 'All' : v}</button>)}</div>
      <form className="receiptSearch" onSubmit={submitSearch}>
        <label><span>Search receipts</span><input value={search} onChange={e => setSearch(e.target.value)} placeholder="Receipt, vehicle, driver, transporter, pallet…" /></label>
        <button type="submit">Search</button>
        {search && <button type="button" className="smallLink" onClick={clearSearch}>Clear</button>}
      </form>
    </div>}

    <div className="receiptList">
      {rows.length === 0 && <Empty text={canFilter ? (search ? 'No receipts match your search and filters.' : 'No receipts match these filters.') : 'No receipts yet.'} />}
      {rows.map(r => <div className={`receiptCard ${r.status === 'CANCELLED' ? 'cancelled' : ''}`} key={r.id}>
        <div className="receiptTop"><div><b>{r.receiptNumber}</b><span className={`badge ${r.status === 'CANCELLED' ? 'red' : 'green'}`}>{r.status}</span>{r.wasReversed && <span className="badge blue">REVERSED</span>}{me.role === 'Viewer' && r.terminal && <span className="badge">{r.terminal}</span>}</div><strong className="clock">🕒 {formatTimestamp(r.submittedAtUtc)}</strong></div>
        <div className="receiptInfo">
          <span><small>Transporter</small>{r.transporter}</span>
          <span><small>Vehicle</small><b>{r.vehicle}</b></span>
          <span><small>Driver</small>{r.driver}</span>
          <span><small>Direction</small><b>{r.direction}</b></span>
          <span className="receiptAmounts"><small>Pallets</small><b>{r.items.map(i => `${i.quantity} ${i.palletType}`).join(', ') || '—'}</b></span>
        </div>
        <div className="receiptActions">
          {(r.status === 'CANCELLED' || r.wasReversed) && <button className="infoBtn" onClick={() => setDetail(r)}>ⓘ Receipt history</button>}
          {canManage && r.status === 'ACTIVE' && <button className="dangerGhost" onClick={() => cancel(r)}>Cancel</button>}
          {canManage && r.status === 'CANCELLED' && <button onClick={() => reverse(r)}>↶ Reverse cancellation</button>}
        </div>
      </div>)}
    </div>

    {detail && <Modal title={`Receipt history · ${detail.receiptNumber}`} close={() => setDetail(null)}>
      <div className="detailGrid"><span><small>Status</small><b>{detail.status}</b></span><span><small>Receipt date</small>{detail.businessDate}</span>{detail.terminal && <span><small>Terminal</small>{detail.terminal}</span>}<span><small>Current reason</small>{detail.cancelReason || '—'}</span><span><small>Cancelled at</small>{formatTimestamp(detail.cancelledAtUtc) || '—'}</span></div>
      <h3>Audit history</h3>
      {detail.actions?.length ? detail.actions.map(a => <div className="historyRow" key={a.id}><b>{a.action.replaceAll('_', ' ')}</b><span>{a.user}</span><span>{formatTimestamp(a.createdAtUtc)}</span><p>{a.reason}</p></div>) : <Empty text="No receipt history." />}
      {canManage && detail.status === 'CANCELLED' && <button className="primary" onClick={() => reverse(detail)}>Reverse cancellation</button>}
    </Modal>}
  </section>
}

function Warnings({ me }) {
  const [onlyOpen, setOnlyOpen] = useState(true)
  const [search, setSearch] = useState('')
  const [data, setData] = useState({ openCount: 0, warnings: [] })
  const [error, setError] = useState('')

  async function load(open = onlyOpen, term = search) {
    const p = new URLSearchParams({ unacknowledgedOnly: String(open), limit: '500' })
    if (term.trim()) p.set('search', term.trim())
    setData(await api(`/warnings?${p}`))
  }

  useEffect(() => {
    const timer = setTimeout(() => {
      load(onlyOpen, search).catch(e => setError(e.message))
    }, 250)
    return () => clearTimeout(timer)
  }, [onlyOpen, search])

  async function ack(id) {
    try {
      await api(`/warnings/${id}/acknowledge`, { method: 'POST', body: '{}' })
      await load(onlyOpen, search)
    } catch (e) { setError(e.message) }
  }

  async function ackAll() {
    if (!data.openCount) return
    if (!window.confirm(`Acknowledge all ${data.openCount} open warnings for ${me.terminalCode}?`)) return
    try {
      const result = await api('/warnings/acknowledge-all', { method: 'POST', body: '{}' })
      await load(onlyOpen, search)
      setError('')
      if (result.acknowledged === 0) await load(onlyOpen, search)
    } catch (e) { setError(e.message) }
  }

  return <section>
    <div className="pageTitle"><div><h1>Warnings · {me.terminalCode}</h1><p>Only warnings belonging to terminal {me.terminalCode} are shown. Only Admin can configure warning rules.</p></div><div className="warningCount">{data.openCount} open</div></div>
    {error && <div className="error">{error}</div>}
    <div className="warningToolbar">
      <div className="segmented warningTabs"><button className={onlyOpen ? 'active' : ''} onClick={() => setOnlyOpen(true)}>Open warnings</button><button className={!onlyOpen ? 'active' : ''} onClick={() => setOnlyOpen(false)}>All warnings</button></div>
      <button className="primary" disabled={!data.openCount} onClick={ackAll}>✓ Acknowledge all ({data.openCount})</button>
      <label className="warningSearch"><span>Search warnings</span><input value={search} onChange={e => setSearch(e.target.value)} placeholder="Receipt, vehicle, driver, transporter, warning…" /></label>
    </div>
    <div className="warningList">
      {data.warnings.length === 0 && <Empty text={search ? 'No warnings match your search.' : 'No warnings to show.'} />}
      {data.warnings.map(w => <div className={`warningCard ${w.severity}`} key={w.id}>
        <div className="warningHead"><div><span className="badge amber">{w.type.replaceAll('_', ' ')}</span>{w.receiptNumber && <b>{w.receiptNumber}</b>}</div><span>{formatTimestamp(w.createdAtUtc)}</span></div>
        <p>{w.message}</p>
        {(w.vehicle || w.driver || w.transporter) && <div className="warningReceiptMeta">{[w.vehicle, w.driver, w.transporter].filter(Boolean).join(' · ')}</div>}
        <div className="warningMeta">Triggered by {w.triggeredBy}{w.acknowledgedAtUtc ? ` · Acknowledged by ${w.acknowledgedBy} at ${formatTimestamp(w.acknowledgedAtUtc)}` : ''}</div>
        {!w.acknowledgedAtUtc && <button onClick={() => ack(w.id)}>✓ Acknowledge</button>}
      </div>)}
    </div>
  </section>
}


function PeriodFilter({ preset, setPreset, from, setFrom, to, setTo }) {
  function change(value) {
    setPreset(value)
    if (value !== 'custom') { const d = periodDates(value); setFrom(d.from); setTo(d.to) }
  }
  return <div className="periodBar">
    <select value={preset} onChange={e => change(e.target.value)}>
      <option value="today">Today</option><option value="yesterday">Yesterday</option><option value="thisWeek">This week</option>
      <option value="previousWeek">Last week</option><option value="thisMonth">This month</option><option value="lastMonth">Last month</option>
      <option value="thisYear">This year</option><option value="lastYear">Last year</option><option value="custom">Custom</option>
    </select>
    <input type="date" value={from} onChange={e => { setPreset('custom'); setFrom(e.target.value) }} />
    <span>→</span>
    <input type="date" value={to} onChange={e => { setPreset('custom'); setTo(e.target.value) }} />
  </div>
}

function LinehaulRegister({ me }) {
  const [setup, setSetup] = useState(null)
  const [unitReference, setUnitReference] = useState('')
  const [palletReceiptNumber, setPalletReceiptNumber] = useState('')
  const [palletCount, setPalletCount] = useState('')
  const [fromTerminalId, setFromTerminalId] = useState(String(me.terminalId))
  const [toTerminalId, setToTerminalId] = useState('')
  const [commentOptionId, setCommentOptionId] = useState('')
  const [freeComment, setFreeComment] = useState('')
  const [businessDate, setBusinessDate] = useState(dateInput())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState(null)

  useEffect(() => {
    api('/linehaul/setup').then(r => {
      setSetup(r)
      const other = r.terminals.find(t => Number(t.id) !== Number(me.terminalId))
      if (other) setToTerminalId(String(other.id))
    }).catch(e => setError(e.message))
  }, [me.terminalId])

  async function submit() {
    setError(''); setSuccess(null)
    if (!palletReceiptNumber.trim()) return setError('Enter pallet receipt number.')
    if (palletCount === '' || Number(palletCount) < 0) return setError('Enter number of pallets. 0 is allowed.')
    if (!fromTerminalId || !toTerminalId) return setError('Choose From and To terminal.')
    if (fromTerminalId === toTerminalId) return setError('From and To terminal must be different.')
    if (Number(fromTerminalId) !== Number(me.terminalId) && Number(toTerminalId) !== Number(me.terminalId)) return setError(`One side must be your terminal ${me.terminalCode}.`)
    setBusy(true)
    try {
      const result = await api('/linehaul/receipts', {
        method: 'POST',
        body: JSON.stringify({
          unitReference, palletReceiptNumber, palletCount: Number(palletCount),
          fromTerminalId: Number(fromTerminalId), toTerminalId: Number(toTerminalId),
          commentOptionId: commentOptionId ? Number(commentOptionId) : null,
          freeComment, businessDate
        })
      })
      setSuccess(result)
      setUnitReference(''); setPalletReceiptNumber(''); setPalletCount(''); setCommentOptionId(''); setFreeComment('')
    } catch (e) { setError(e.message) } finally { setBusy(false) }
  }

  if (!setup) return <Loading />
  return <section>
    <div className="pageTitle"><div><h1>Linehaul · Register</h1><p>Register pallets moved between terminals. Manual registrations must involve {me.terminalCode}.</p></div></div>
    {error && <div className="error">{error}</div>}
    {success && <div className="success"><b>{success.receiptNumber}</b> registered: {success.fromTerminal} → {success.toTerminal}, {success.palletCount} pallets · Pallekvittering {success.palletReceiptNumber}.</div>}
    <div className="card moduleFormCard"><div className="formGrid linehaulForm">
      <label>Container / trailer no. or text <span className="muted small">(optional)</span><input value={unitReference} onChange={e => setUnitReference(e.target.value)} placeholder="Optional – e.g. TTR12345" /></label>
      <label>Pallekvitteringsnummer <span className="requiredMark">*</span><input value={palletReceiptNumber} onChange={e => setPalletReceiptNumber(e.target.value)} placeholder="Must be unique, e.g. PK-123456" /></label>
      <label>Number of pallets<input type="number" min="0" value={palletCount} onChange={e => setPalletCount(e.target.value)} /></label>
      <label>From terminal<select value={fromTerminalId} onChange={e => setFromTerminalId(e.target.value)}>{setup.terminals.map(t => <option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>
      <label>To terminal<select value={toTerminalId} onChange={e => setToTerminalId(e.target.value)}>{setup.terminals.map(t => <option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>
      <label>Date<input type="date" value={businessDate} onChange={e => setBusinessDate(e.target.value)} /></label>
      <label>Selectable comment<select value={commentOptionId} onChange={e => setCommentOptionId(e.target.value)}><option value="">No standard comment</option>{setup.comments.map(c => <option key={c.id} value={c.id}>{c.text}</option>)}</select><small className="muted">Options belong to {me.terminalCode} and do not depend on From/To.</small></label>
      <label className="fullSpan">Free comment<textarea value={freeComment} onChange={e => setFreeComment(e.target.value)} placeholder="Optional comment" /></label>
      <div className="fullSpan"><button className="primary big" disabled={busy} onClick={submit}>{busy ? 'Registering…' : 'Register linehaul receipt'}</button></div>
    </div></div>
  </section>
}

function LinehaulReceipts({ me }) {
  const initial = periodDates('thisMonth')
  const [setup, setSetup] = useState(null), [rows, setRows] = useState([])
  const [preset, setPreset] = useState('thisMonth'), [from, setFrom] = useState(initial.from), [to, setTo] = useState(initial.to)
  const [direction, setDirection] = useState('all'), [recordStatus, setRecordStatus] = useState('all')
  const [fromTerminalId, setFromTerminalId] = useState(''), [toTerminalId, setToTerminalId] = useState(''), [search, setSearch] = useState('')
  const [error, setError] = useState(''), [message, setMessage] = useState('')

  async function load() {
    setError('')
    try {
      const qs = new URLSearchParams({ from, to, direction, status: recordStatus })
      if (fromTerminalId) qs.set('fromTerminalId', fromTerminalId)
      if (toTerminalId) qs.set('toTerminalId', toTerminalId)
      if (search.trim()) qs.set('search', search.trim())
      const r = await api(`/linehaul/receipts?${qs}`)
      setRows(r.rows || [])
    } catch (e) { setError(e.message) }
  }

  async function cancelRow(row) {
    const reason = window.prompt(`Cancel ${row.receiptNumber}?\nReason:`, '')
    if (reason === null) return
    setError(''); setMessage('')
    try {
      await api(`/linehaul/receipts/${row.id}/cancel`, { method: 'POST', body: JSON.stringify({ reason }) })
      setMessage(`${row.receiptNumber} cancelled. Cancelled receipts are excluded from statistics.`)
      await load()
    } catch (e) { setError(e.message) }
  }

  async function deleteRow(row) {
    if (!window.confirm(`PERMANENTLY DELETE ${row.receiptNumber}?\n\nThis cannot be undone and will remove the record from the database.`)) return
    setError(''); setMessage('')
    try {
      await api(`/linehaul/receipts/${row.id}`, { method: 'DELETE' })
      setMessage(`${row.receiptNumber} permanently deleted.`)
      await load()
    } catch (e) { setError(e.message) }
  }

  useEffect(() => { api('/linehaul/setup').then(setSetup).catch(e => setError(e.message)) }, [])
  useEffect(() => { if (setup) load() }, [setup, from, to, direction, recordStatus, fromTerminalId, toTerminalId])
  if (!setup) return <Loading />

  return <section><div className="pageTitle"><div><h1>Linehaul · Receipts · {me.terminalCode}</h1><p>Linehaul records visible to {me.terminalCode}. Cancelled records remain visible but do not count in statistics.</p></div></div>
    {message && <div className="success">{message}</div>}{error && <div className="error">{error}</div>}
    <div className="card filterCard"><PeriodFilter {...{preset,setPreset,from,setFrom,to,setTo}} /><div className="filterRow">
      <select value={direction} onChange={e => setDirection(e.target.value)}><option value="all">All directions</option><option value="sent">Sent from {me.terminalCode}</option><option value="received">Received by {me.terminalCode}</option></select>
      <select value={recordStatus} onChange={e => setRecordStatus(e.target.value)}><option value="all">Active + cancelled</option><option value="active">Active only</option><option value="cancelled">Cancelled only</option></select>
      <select value={fromTerminalId} onChange={e => setFromTerminalId(e.target.value)}><option value="">All From terminals</option>{setup.terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select>
      <select value={toTerminalId} onChange={e => setToTerminalId(e.target.value)}><option value="">All To terminals</option>{setup.terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select>
      <input placeholder="Search container, pallekvittering or comment" value={search} onChange={e => setSearch(e.target.value)} onKeyDown={e => e.key === 'Enter' && load()} /><button onClick={load}>Search</button>
    </div></div>
    <div className="card tableCard"><div className="tableWrap"><table><thead><tr><th>Date</th><th>Receipt</th><th>Container / trailer</th><th>Pallekvittering no.</th><th>From</th><th>To</th><th>Pallets</th><th>Standard comment</th><th>Comment</th><th>Status</th><th>Submitted by</th><th className="stickyActionCol">Actions</th></tr></thead><tbody>
    {rows.map(r => <tr key={r.id} className={r.status === 'CANCELLED' ? 'cancelledTableRow' : ''}><td>{formatDate(r.businessDate)}</td><td><b>{r.receiptNumber}</b></td><td>{r.unitReference || '—'}</td><td>{r.palletReceiptNumber || '—'}</td><td>{r.fromTerminal}</td><td>{r.toTerminal}</td><td><b>{r.palletCount}</b></td><td>{r.standardComment || '—'}</td><td>{r.freeComment || '—'}</td><td>{r.status === 'CANCELLED' ? <><span className="badge red">CANCELLED</span>{r.cancelReason && <div className="muted small">{r.cancelReason}</div>}</> : <span className="badge green">ACTIVE</span>}</td><td>{r.submittedBy}</td><td className="stickyActionCol">{r.canManage && <div className="tableActions">{r.status !== 'CANCELLED' && <button className="dangerGhost" onClick={() => cancelRow(r)}>Cancel</button>}<button className="dangerGhost" onClick={() => deleteRow(r)}>Delete permanently</button></div>}</td></tr>)}
    </tbody></table></div>{rows.length === 0 && <Empty text="No linehaul receipts in this period." />}</div>
  </section>
}

function LinehaulStats({ me }) {
  const initial = periodDates('thisMonth')
  const [preset,setPreset]=useState('thisMonth'), [from,setFrom]=useState(initial.from), [to,setTo]=useState(initial.to), [data,setData]=useState(null), [error,setError]=useState('')
  useEffect(() => { setError(''); api(`/linehaul/statistics?from=${from}&to=${to}`).then(setData).catch(e => setError(e.message)) }, [from,to])
  return <section><div className="pageTitle"><div><h1>Linehaul · Statistics · {me.terminalCode}</h1><p>Positive balance means {me.terminalCode} has sent more pallets than it has received and is therefore in plus.</p></div></div>
    {error && <div className="error">{error}</div>}<div className="card filterCard"><PeriodFilter {...{preset,setPreset,from,setFrom,to,setTo}} /></div>
    {!data ? <Loading /> : <><div className="statsCards"><StatCard label="Sent pallets" value={data.totalSentPallets}/><StatCard label="Received pallets" value={data.totalReceivedPallets}/><StatCard label={`${me.terminalCode} global balance`} value={signed(data.globalBalance)} cls={data.globalBalance >= 0 ? 'positive':'negative'}/><StatCard label="Linehaul movements" value={data.totalSentLoads + data.totalReceivedLoads}/></div>
      <div className="card tableCard"><div className="tableWrap"><table><thead><tr><th>Other terminal</th><th>Sent loads</th><th>Received loads</th><th>Sent pallets</th><th>Received pallets</th><th>{me.terminalCode} balance</th></tr></thead><tbody>{data.rows.map(r => <tr key={r.terminalId}><td><b>{r.terminal}</b></td><td>{r.sentLoads}</td><td>{r.receivedLoads}</td><td>{r.sentPallets}</td><td>{r.receivedPallets}</td><td className={r.balance >= 0 ? 'positive':'negative'}><b>{signed(r.balance)}</b></td></tr>)}</tbody></table></div>{data.rows.length===0 && <Empty text="No linehaul activity in this period."/>}</div></>}
  </section>
}

function LinehaulExport({ me }) {
  const initial=periodDates('thisMonth'); const [preset,setPreset]=useState('thisMonth'), [from,setFrom]=useState(initial.from), [to,setTo]=useState(initial.to), [type,setType]=useState('complete'), [format,setFormat]=useState('xlsx'), [busy,setBusy]=useState(false), [error,setError]=useState('')
  async function download(){ setBusy(true);setError('');try{const actualFormat=type==='complete'?'xlsx':format;await downloadApiFile(`/linehaul/export?from=${from}&to=${to}&type=${type}&format=${actualFormat}`,`Linehaul_${me.terminalCode}.${actualFormat}`)}catch(e){setError(e.message)}finally{setBusy(false)}}
  return <section><div className="pageTitle"><div><h1>Linehaul · Export</h1><p>Export terminal-to-terminal pallet movements for {me.terminalCode}.</p></div></div>{error&&<div className="error">{error}</div>}<div className="card exportCard moduleExport"><PeriodFilter {...{preset,setPreset,from,setFrom,to,setTo}}/><label>Export<select value={type} onChange={e=>setType(e.target.value)}><option value="complete">Complete Excel report</option><option value="receipts">Receipt details</option><option value="summary">Terminal summary</option></select></label>{type!=='complete'&&<label>Format<select value={format} onChange={e=>setFormat(e.target.value)}><option value="xlsx">Excel (.xlsx)</option><option value="csv">CSV</option></select></label>}<button className="primary" disabled={busy} onClick={download}>{busy?'Preparing…':'Download export'}</button></div></section>
}

function importValue(value) {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value)) return formatDate(value.slice(0, 10))
  return String(value)
}

function ImportRowsTable({ title, rows, truncated }) {
  if (!rows?.length) return null
  const labels = {
    row: 'Excel/CSV row', receiptNumber: 'Receipt', controlNumber: 'Control', date: 'Date', fromTerminal: 'From terminal', toTerminal: 'To terminal',
    containerTrailer: 'Container / trailer', palletReceiptNumber: 'Pallekvittering no.', pallets: 'Pallets', standardComment: 'Selectable comment', comment: 'Comment',
    palletReceiptReceived: 'Pallet receipt received', receiptPallets: 'Receipt pallets', actualPallets: 'Actual pallets', result: 'Result'
  }
  const keys = Object.keys(rows[0] || {})
  return <div className="importRowsBlock">
    <h3>{title}</h3>
    <div className="tableWrap"><table className="importPreviewTable"><thead><tr>{keys.map(k => <th key={k}>{labels[k] || k}</th>)}</tr></thead><tbody>
    {rows.map((r, i) => <tr key={`${r.row || r.receiptNumber || r.controlNumber || i}-${i}`}>{keys.map(k => <td key={k}>{k === 'result' ? receivedStatusLabel(r[k]) : importValue(r[k])}</td>)}</tr>)}
    </tbody></table></div>
    {truncated && <p className="muted small">Only the first 500 rows are displayed here. All validated rows will still be imported after confirmation.</p>}
  </div>
}

function ImportResult({ result }) {
  if (!result) return null
  const preview = result.preview === true
  return <div className="card importResult">
    <div className="statsCards compactStats">
      <StatCard label="Rows read" value={result.rowsRead || 0}/>
      {preview ? <StatCard label="Ready to import" value={result.readyToImport || 0} cls="positive"/> : <StatCard label="Imported" value={result.imported || 0} cls="positive"/>}
      <StatCard label="Rows not imported" value={result.rejected || 0} cls={(result.rejected || 0) ? 'negative' : ''}/>
      {!preview && result.redWarningsCreated !== undefined && <StatCard label="Red warnings created" value={result.redWarningsCreated || 0} cls={(result.redWarningsCreated || 0) ? 'negative' : ''}/>}
    </div>
    {preview && <ImportRowsTable title="Rows that will be imported" rows={result.previewRows} truncated={result.previewRowsTruncated}/>}
    {!preview && <ImportRowsTable title="Imported rows" rows={result.importedRows} truncated={result.importedRowsTruncated}/>}
    {result.skippedDuplicates > 0 && <div className="warningInline">{result.skippedDuplicates} matching existing row(s) were skipped as duplicates.</div>}
    {result.warnings?.length > 0 && <div className="importIssues"><h3>Accepted with warnings</h3>{result.warnings.map((x,i)=><div key={`w-${i}`} className="importIssue warningInline"><b>Row {x.row}:</b> {x.message}</div>)}</div>}
    {result.issues?.length > 0 && <div className="importIssues"><h3>Rows not imported</h3>{result.issues.map((x,i)=><div key={`e-${i}`} className="importIssue error"><b>Row {x.row}:</b> {x.message}</div>)}{result.issueListTruncated&&<p className="muted">Only the first 200 issues are shown.</p>}</div>}
  </div>
}

function ImportSchema({ rows }) {
  return <div className="tableWrap"><table className="importSchema"><thead><tr><th>Column</th><th>Required</th><th>Example</th><th>Meaning</th></tr></thead><tbody>{rows.map(r=><tr key={r[0]}><td><code>{r[0]}</code></td><td>{r[1]}</td><td>{r[2]}</td><td>{r[3]}</td></tr>)}</tbody></table></div>
}

function HistoricalImport({ me, title, endpoint, templateBase, schema, note }) {
  const [file,setFile]=useState(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[result,setResult]=useState(null)

  async function send(confirm) {
    if(!file) return setError('Choose an .xlsx or .csv file first.')
    setBusy(true);setError('')
    if(!confirm) setResult(null)
    try {
      const body=new FormData(); body.append('file',file); body.append('confirm', confirm ? 'true' : 'false')
      setResult(await api(endpoint,{method:'POST',body}))
    } catch(e) { setError(e.message) } finally { setBusy(false) }
  }

  function chooseFile(e) {
    setFile(e.target.files?.[0]||null); setResult(null); setError('')
  }

  return <section><div className="pageTitle"><div><h1>{title} · Import · {me.terminalCode}</h1><p>Nothing is written to the database until you review the preview and explicitly confirm the import.</p></div></div>{error&&<div className="error">{error}</div>}
    <div className="card importCard"><h2>Import format</h2><p className="muted">Excel (.xlsx) and CSV are supported; CSV may use comma or semicolon separators. {note}</p><ImportSchema rows={schema}/>
      <div className="templateButtons"><button onClick={()=>downloadApiFile(`${templateBase}?format=xlsx`,`${title}_Import_Template.xlsx`)}>Download Excel template</button><button onClick={()=>downloadApiFile(`${templateBase}?format=csv`,`${title}_Import_Template.csv`)}>Download CSV template</button></div>
    </div>
    <div className="card importCard"><h2>1. Select and preview old data</h2><input className="fileInput" type="file" accept=".xlsx,.csv,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" onChange={chooseFile}/>{file&&<p className="muted small">Selected: {file.name}</p>}
      <button className="primary" disabled={busy||!file} onClick={()=>send(false)}>{busy?'Checking…':'Preview import'}</button>
      <p className="muted small">Preview validates terminals, duplicates, dates and quantities. It does not save anything.</p>
    </div>
    <ImportResult result={result}/>
    {result?.preview && <div className="card importConfirmCard"><h2>2. Confirm import</h2><p><b>{result.readyToImport || 0}</b> row(s) will be written to the database. <b>{result.rejected || 0}</b> row(s) will not be imported.</p><button className="primary big" disabled={busy || !(result.readyToImport > 0)} onClick={()=>send(true)}>{busy?'Importing…':`Yes, import ${result.readyToImport || 0} row(s)`}</button><p className="muted small">Pressing this button is the final confirmation.</p></div>}
  </section>
}

function LinehaulImport({ me }) {
  const schema=[
    ['Date','Yes','2026-08-28','Business date'],
    ['ContainerTrailer','No','TTR12345','Optional container/trailer number or text'],
    ['PalletReceiptNumber','Legacy: optional','PK-123456','If supplied it must be unique in the Linehaul database'],
    ['Pallets','Yes','33','Number of pallets'],
    ['FromTerminal','At least From or To','SRD / SRD123 / Sandefjord','Existing terminal Code, Name or Alias; blank side is inferred as your terminal'],
    ['ToTerminal','At least From or To','ARE','Existing terminal Code, Name or Alias; blank side is inferred as your terminal'],
    ['StandardComment','No','Night linehaul','Historical selectable comment text'],
    ['Comment','No','Old Excel import','Free text']
  ]
  return <HistoricalImport me={me} title="Linehaul" endpoint="/linehaul/import" templateBase="/linehaul/import-template" schema={schema} note={`Terminal matching accepts Code, Name and aliases. Values such as SRD123 can resolve to SRD. Import rows may reference any terminals in the Admin terminal list. Blank PalletReceiptNumber is accepted only for legacy history.`}/>
}

function ReceivedControlImport({ me }) {
  const schema=[
    ['Date','Yes','2026-08-28','Business date'],
    ['FromTerminal','Yes','ARE / ARE123 / Arendal','Existing terminal Code, Name or Alias'],
    ['ContainerTrailer','Yes','TTR12345','Container/trailer number or text'],
    ['PalletReceiptReceived','Yes','Yes','Yes/No, Ja/Nei, true/false or 1/0'],
    ['ReceiptPallets','If received','33','Pallet quantity written on receipt'],
    ['ActualPallets','Yes','31','Actual pallets physically received'],
    ['Comment','No','Damaged seal','Optional free text']
  ]
  return <HistoricalImport me={me} title="MottattKontroll" endpoint="/received-control/import" templateBase="/received-control/import-template" schema={schema} note={`Imported controls belong to receiving terminal ${me.terminalCode}. FromTerminal accepts Code, Name and configured aliases from ${me.terminalCode}’s own Linehaul/Mottatt location list. Red shortages automatically create acknowledgement warnings.`}/>
}

function receivedStatusLabel(value) {
  if (value === 'NO_RECEIPT') return 'No pallet receipt'
  if (value === 'RECEIPT_HIGHER') return 'Receipt amount higher than actual'
  if (value === 'RECEIPT_LOWER') return 'Receipt amount lower than actual'
  if (value === 'EXACT') return 'Exact'
  return value
}
function receivedRowClass(value, colours=true){ if(!colours)return ''; return value==='NO_RECEIPT'?'rcBlue':value==='RECEIPT_HIGHER'?'rcRed':value==='RECEIPT_LOWER'?'rcOrange':value==='EXACT'?'rcGreen':'' }

function ReceivedControlRegister({ me }) {
  const [setup,setSetup]=useState(null),[fromTerminalId,setFromTerminalId]=useState(''),[unitReference,setUnitReference]=useState(''),[comment,setComment]=useState(''),[received,setReceived]=useState(true),[receiptQty,setReceiptQty]=useState(''),[actualQty,setActualQty]=useState(''),[businessDate,setBusinessDate]=useState(dateInput()),[busy,setBusy]=useState(false),[error,setError]=useState(''),[success,setSuccess]=useState(null)
  useEffect(()=>{api('/received-control/setup').then(r=>{setSetup(r);const other=r.terminals.find(t=>Number(t.id)!==Number(me.terminalId));if(other)setFromTerminalId(String(other.id))}).catch(e=>setError(e.message))},[me.terminalId])
  async function submit(){setError('');setSuccess(null);if(!fromTerminalId)return setError('Choose From terminal.');if(actualQty===''||Number(actualQty)<0)return setError('Enter actual number of pallets.');if(received&&(receiptQty===''||Number(receiptQty)<0))return setError('Enter the quantity written on the pallet receipt.');setBusy(true);try{const r=await api('/received-control/entries',{method:'POST',body:JSON.stringify({fromTerminalId:Number(fromTerminalId),unitReference,comment,palletReceiptReceived:received,receiptPalletCount:received?Number(receiptQty):null,actualPalletCount:Number(actualQty),businessDate})});setSuccess(r);setUnitReference('');setComment('');setReceiptQty('');setActualQty('')}catch(e){setError(e.message)}finally{setBusy(false)}}
  if(!setup)return <Loading/>
  return <section><div className="pageTitle"><div><h1>MottattKontroll · Register · {me.terminalCode}</h1><p>Register which terminal the load came from and compare the pallet receipt with the pallets physically received.</p></div></div>{error&&<div className="error">{error}</div>}{success&&<div className={`success receivedSuccess ${receivedRowClass(success.result)}`}><b>{success.controlNumber}</b> registered · {receivedStatusLabel(success.result)}</div>}
    <div className="card moduleFormCard"><div className="formGrid receivedForm">
      <label>From terminal<select value={fromTerminalId} onChange={e=>setFromTerminalId(e.target.value)}><option value="">Choose terminal</option>{setup.terminals.filter(t=>Number(t.id)!==Number(me.terminalId)).map(t=><option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>
      <label>Container / trailer no.<input value={unitReference} onChange={e=>setUnitReference(e.target.value)} placeholder="Container/trailer reference"/></label>
      <label>Date<input type="date" value={businessDate} onChange={e=>setBusinessDate(e.target.value)}/></label>
      <label className="fullSpan">Comment <span className="muted small">(optional)</span><textarea value={comment} onChange={e=>setComment(e.target.value)} placeholder="Optional comment"/></label>
      <label className="checkCard fullSpan"><input type="checkbox" checked={received} onChange={e=>{setReceived(e.target.checked);if(!e.target.checked)setReceiptQty('')}}/><span><b>Pallet receipt received</b><small>Uncheck when no pallet receipt was received with the container/trailer.</small></span></label>
      {received&&<label>Pallets written on receipt<input type="number" min="0" value={receiptQty} onChange={e=>setReceiptQty(e.target.value)}/></label>}<label>Actual pallets received<input type="number" min="0" value={actualQty} onChange={e=>setActualQty(e.target.value)}/></label>
      <div className="fullSpan rcLegend"><span className="legendBlue">Blue: no receipt</span><span className="legendRed">Red: receipt &gt; actual</span><span className="legendOrange">Orange: receipt &lt; actual</span><span className="legendGreen">Green: exact</span></div>
      <div className="fullSpan"><button className="primary big" disabled={busy} onClick={submit}>{busy?'Registering…':'Register received control'}</button></div></div></div>
  </section>
}

function ReceivedControlStats({ me }) {
  const initial=periodDates('thisMonth')
  const[preset,setPreset]=useState('thisMonth'),[from,setFrom]=useState(initial.from),[to,setTo]=useState(initial.to),[status,setStatus]=useState('all'),[recordStatus,setRecordStatus]=useState('all'),[search,setSearch]=useState(''),[colours,setColours]=useState(true),[data,setData]=useState(null),[error,setError]=useState(''),[message,setMessage]=useState('')
  async function load(){setError('');try{const qs=new URLSearchParams({from,to,status,recordStatus});if(search.trim())qs.set('search',search.trim());setData(await api(`/received-control/statistics?${qs}`))}catch(e){setError(e.message)}}
  async function cancelRow(row){const reason=window.prompt(`Cancel ${row.controlNumber}?\nReason:`,'');if(reason===null)return;setError('');setMessage('');try{await api(`/received-control/entries/${row.id}/cancel`,{method:'POST',body:JSON.stringify({reason})});setMessage(`${row.controlNumber} cancelled. It no longer counts in statistics or warnings.`);await load()}catch(e){setError(e.message)}}
  async function deleteRow(row){if(!window.confirm(`PERMANENTLY DELETE ${row.controlNumber}?\n\nThis also removes warnings connected to this control and cannot be undone.`))return;setError('');setMessage('');try{await api(`/received-control/entries/${row.id}`,{method:'DELETE'});setMessage(`${row.controlNumber} permanently deleted.`);await load()}catch(e){setError(e.message)}}
  useEffect(()=>{load()},[from,to,status,recordStatus])
  return <section><div className="pageTitle"><div><h1>MottattKontroll · Statistics · {me.terminalCode}</h1><p>Location names are resolved from this operating terminal’s Linehaul/Mottatt location list; renaming a location updates the statistics display.</p></div><label className="colourToggle"><input type="checkbox" checked={colours} onChange={e=>setColours(e.target.checked)}/> Enable colours</label></div>{message&&<div className="success">{message}</div>}{error&&<div className="error">{error}</div>}
    <div className="card filterCard"><PeriodFilter {...{preset,setPreset,from,setFrom,to,setTo}}/><div className="filterRow"><select value={status} onChange={e=>setStatus(e.target.value)}><option value="all">All results</option><option value="NO_RECEIPT">Blue · No receipt</option><option value="RECEIPT_HIGHER">Red · Receipt higher</option><option value="RECEIPT_LOWER">Orange · Receipt lower</option><option value="EXACT">Green · Exact</option></select><select value={recordStatus} onChange={e=>setRecordStatus(e.target.value)}><option value="all">Active + cancelled</option><option value="active">Active only</option><option value="cancelled">Cancelled only</option></select><input placeholder="Search from terminal, container or comment" value={search} onChange={e=>setSearch(e.target.value)} onKeyDown={e=>e.key==='Enter'&&load()}/><button onClick={load}>Search</button></div></div>
    {!data?<Loading/>:<><div className="statsCards"><StatCard label="Active controls" value={data.total}/><StatCard label="No receipt" value={data.noReceipt}/><StatCard label="Red shortages" value={data.receiptHigher} cls="negative"/><StatCard label="Exact" value={data.exact} cls="positive"/></div><div className="card tableCard"><div className="tableWrap"><table><thead><tr><th>Date</th><th>Control</th><th>From terminal</th><th>Container / trailer</th><th>Comment</th><th>Pallet receipt</th><th>Receipt qty</th><th>Actual qty</th><th>Difference</th><th>Result</th><th>Status</th><th>Submitted by</th><th className="stickyActionCol">Actions</th></tr></thead><tbody>{data.rows.map(r=><tr key={r.id} className={r.status==='CANCELLED'?'cancelledTableRow':receivedRowClass(r.result,colours)}><td>{formatDate(r.businessDate)}</td><td><b>{r.controlNumber}</b></td><td><b>{r.fromTerminal || '—'}</b></td><td>{r.unitReference || '—'}</td><td>{r.comment || '—'}</td><td>{r.palletReceiptReceived?'Yes':'No'}</td><td>{r.receiptPalletCount??'—'}</td><td><b>{r.actualPalletCount}</b></td><td>{r.difference??'—'}</td><td><b>{receivedStatusLabel(r.result)}</b></td><td>{r.status==='CANCELLED'?<><span className="badge red">CANCELLED</span>{r.cancelReason&&<div className="muted small">{r.cancelReason}</div>}</>:<span className="badge green">ACTIVE</span>}</td><td>{r.submittedBy}</td><td className="stickyActionCol">{r.canManage&&<div className="tableActions">{r.status!=='CANCELLED'&&<button className="dangerGhost" onClick={()=>cancelRow(r)}>Cancel</button>}<button className="dangerGhost" onClick={()=>deleteRow(r)}>Delete permanently</button></div>}</td></tr>)}</tbody></table></div>{data.rows.length===0&&<Empty text="No controls in this period."/>}</div></>}
  </section>
}

function ReceivedControlWarnings({ me }) {
  const[unack,setUnack]=useState(true),[rows,setRows]=useState([]),[error,setError]=useState('')
  async function load(){setError('');try{const r=await api(`/received-control/warnings?unacknowledgedOnly=${unack}`);setRows(r.warnings||[])}catch(e){setError(e.message)}}
  useEffect(()=>{load()},[unack])
  async function ack(id){try{await api(`/received-control/warnings/${id}/acknowledge`,{method:'POST',body:'{}'});await load()}catch(e){setError(e.message)}}
  async function ackAll(){const open=rows.filter(w=>!w.acknowledgedAtUtc).length;if(!open)return;if(!window.confirm(`Acknowledge all ${open} open MottattKontroll warnings for ${me.terminalCode}?`))return;try{await api('/received-control/warnings/acknowledge-all',{method:'POST',body:'{}'});await load()}catch(e){setError(e.message)}}
  return <section><div className="pageTitle"><div><h1>MottattKontroll · Warnings · {me.terminalCode}</h1><p>Red quantity shortages require acknowledgement.</p></div><div className="pageTitleActions"><button className="primary" disabled={!rows.some(w=>!w.acknowledgedAtUtc)} onClick={ackAll}>✓ Acknowledge all</button><label className="miniCheck"><input type="checkbox" checked={unack} onChange={e=>setUnack(e.target.checked)}/> Unacknowledged only</label></div></div>{error&&<div className="error">{error}</div>}<div className="warningList">{rows.map(w=><div className="card warningCard rcRed" key={w.id}><div className="warningHead"><div><span className="badge red">SHORTAGE</span><b>{[w.entry?.fromTerminal,w.entry?.unitReference].filter(Boolean).join(' · ')}</b></div><span>{formatTimestamp(w.createdAtUtc)}</span></div><p>{w.message}</p><div className="warningMeta">{w.entry?.controlNumber} · {formatDate(w.entry?.businessDate)}{w.entry?.comment?` · ${w.entry.comment}`:''}{w.acknowledgedAtUtc?` · Acknowledged by ${w.acknowledgedBy}`:''}</div>{!w.acknowledgedAtUtc&&<button onClick={()=>ack(w.id)}>✓ Acknowledge</button>}</div>)}</div>{rows.length===0&&<Empty text="No received-control warnings."/>}</section>
}

function ReceivedControlExport({ me }) {
  const initial=periodDates('thisMonth');const[preset,setPreset]=useState('thisMonth'),[from,setFrom]=useState(initial.from),[to,setTo]=useState(initial.to),[format,setFormat]=useState('xlsx'),[busy,setBusy]=useState(false),[error,setError]=useState('')
  async function download(){setBusy(true);setError('');try{await downloadApiFile(`/received-control/export?from=${from}&to=${to}&format=${format}`,`ReceivedControl_${me.terminalCode}.${format}`)}catch(e){setError(e.message)}finally{setBusy(false)}}
  return <section><div className="pageTitle"><div><h1>MottattKontroll · Export</h1><p>Excel includes both detailed controls and a summary sheet.</p></div></div>{error&&<div className="error">{error}</div>}<div className="card exportCard moduleExport"><PeriodFilter {...{preset,setPreset,from,setFrom,to,setTo}}/><label>Format<select value={format} onChange={e=>setFormat(e.target.value)}><option value="xlsx">Excel (.xlsx)</option><option value="csv">CSV details</option></select></label><button className="primary" disabled={busy} onClick={download}>{busy?'Preparing…':'Download export'}</button></div></section>
}

function UserSettings({ me }) {
  const [s, setS] = useState(null)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [newDriver, setNewDriver] = useState('')
  const [addingDriver, setAddingDriver] = useState(false)
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [changingPassword, setChangingPassword] = useState(false)
  const isTerminalAdmin = me.role === 'Admin' || me.role === 'TerminalAdmin'
  const canChangeOwnPassword = me.role === 'User' || me.role === 'Superuser'

  useEffect(() => {
    api('/me/settings').then(result => {
      setS(result)
      applyTheme(result.theme || me.theme || 'normal')
    }).catch(e => setError(e.message))
  }, [])
  if (!s) return <Loading />

  async function save(next = s, ok = 'Settings saved.') {
    setMessage(''); setError('')
    try {
      const result = await api('/me/settings', { method: 'PUT', body: JSON.stringify(next) })
      setS(result)
      applyTheme(result.theme)
      try {
        const cached = JSON.parse(localStorage.getItem('me') || '{}')
        localStorage.setItem('me', JSON.stringify({ ...cached, theme: result.theme }))
      } catch {}
      setMessage(ok)
      return result
    } catch (e) {
      setError(e.message)
      return null
    }
  }

  async function chooseTheme(theme) {
    const previous = s.theme || 'normal'
    const next = { ...s, theme }
    setS(next)
    applyTheme(theme)
    const result = await save(next, `${THEME_OPTIONS.find(t => t.id === theme)?.name || 'Theme'} theme saved.`)
    if (!result) { setS(s); applyTheme(previous) }
  }

  async function addDriver() {
    const name = newDriver.trim()
    if (!name) return
    setMessage(''); setError(''); setAddingDriver(true)
    try {
      const added = await api('/drivers/quick-add', {
        method: 'POST',
        body: JSON.stringify({ name })
      })
      setNewDriver('')
      setMessage(`Driver ${added.name} is now available for terminal ${me.terminalCode}.`)
    } catch (e) {
      setError(e.status === 403 ? 'Adding driver names has been disabled by an administrator.' : e.message)
    } finally {
      setAddingDriver(false)
    }
  }

  async function changeOwnPassword(e) {
    e.preventDefault()
    setMessage(''); setError('')
    if (!currentPassword) { setError('Enter your current password.'); return }
    if (newPassword.length < 10) { setError('New password must be at least 10 characters.'); return }
    if (newPassword !== confirmPassword) { setError('The new passwords do not match.'); return }
    setChangingPassword(true)
    try {
      await api('/me/password', {
        method: 'PUT',
        body: JSON.stringify({ currentPassword, newPassword })
      })
      setCurrentPassword(''); setNewPassword(''); setConfirmPassword('')
      setMessage('Password changed.')
    } catch (e) {
      setError(e.message)
    } finally {
      setChangingPassword(false)
    }
  }

  const standardThemes = THEME_OPTIONS.filter(t => t.group === 'standard')
  const specialThemes = THEME_OPTIONS.filter(t => t.group === 'special')

  return <section><div className="pageTitle"><div><h1>My settings</h1><p>Personal settings for {me.displayName}. These settings are stored with your account.</p></div></div>
    {message && <div className="success">{message}</div>}{error && <div className="error">{error}</div>}

    <div className="card settingsCard themeSettingsCard">
      <h2>Appearance</h2>
      <p className="muted">Your theme is saved to your PalletControl account and will be restored after login, browser changes and server restarts.</p>
      <h3>Standard themes</h3>
      <div className="themeChoiceGrid">
        {standardThemes.map(t => <button type="button" key={t.id} className={`themeChoice ${s.theme === t.id ? 'selected' : ''} themePreview-${t.id}`} onClick={() => chooseTheme(t.id)}><span className="themePreviewIcon">{t.id === 'dark' ? '🌙' : '☀️'}</span><span><b>{t.name}</b><small>{t.description}</small></span>{s.theme === t.id && <span className="themeSelectedMark">✓</span>}</button>)}
      </div>
      <h3 className="specialThemeTitle">Special themes</h3>
      <div className="themeChoiceGrid">
        {specialThemes.map(t => <button type="button" key={t.id} className={`themeChoice ${s.theme === t.id ? 'selected' : ''} themePreview-${t.id}`} onClick={() => chooseTheme(t.id)}><span className="themePreviewIcon">{t.id === 'terminal' ? '🏭' : '🕵️'}</span><span><b>{t.name}</b><small>{t.description}</small></span>{s.theme === t.id && <span className="themeSelectedMark">✓</span>}</button>)}
      </div>
    </div>

    {canChangeOwnPassword && <form className="card settingsCard passwordChangeCard" onSubmit={changeOwnPassword}>
      <h2>Change password</h2>
      <p className="muted">Change the password for your own PalletControl account. Your current password is required.</p>
      <div className="passwordChangeGrid">
        <label>Current password<input type="password" autoComplete="current-password" value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} /></label>
        <label>New password<input type="password" autoComplete="new-password" value={newPassword} onChange={e => setNewPassword(e.target.value)} /></label>
        <label>Confirm new password<input type="password" autoComplete="new-password" value={confirmPassword} onChange={e => setConfirmPassword(e.target.value)} /></label>
      </div>
      <button type="submit" className="primary" disabled={changingPassword || !currentPassword || !newPassword || !confirmPassword}>{changingPassword ? 'Changing…' : 'Change password'}</button>
    </form>}

    <div className="card settingsCard">
      <h2>Notifications</h2>
      <Toggle label="Monthly milestone notifications" text="Example: You have now brought in over 100 pallets this month." checked={s.showMilestoneNotifications} onChange={v => setS({ ...s, showMilestoneNotifications: v })} />
      <Toggle label="Leaderboard notifications" text="Show current monthly rank and who is ahead/behind after submission." checked={s.showLeaderboardNotifications} onChange={v => setS({ ...s, showLeaderboardNotifications: v })} />
      <Toggle label="Monthly balance notification" text="Show your selected driver's current IN, OUT and balance after submission." checked={s.showBalanceNotifications} onChange={v => setS({ ...s, showBalanceNotifications: v })} />
      <button className="primary" onClick={() => save(s)}>Save notification settings</button>
    </div>

    <div className="card settingsDriverCard">
      <div>
        <h2>Add driver name</h2>
        <p className="muted">Add a driver to terminal {me.terminalCode}. The name will become available on the Register page.</p>
      </div>
      {s.allowUsersAddDrivers ? <div className="settingsDriverForm">
        <input placeholder="Driver name" value={newDriver} onChange={e => setNewDriver(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addDriver() } }} />
        <button type="button" className="primary" disabled={addingDriver || !newDriver.trim()} onClick={addDriver}>{addingDriver ? 'Adding…' : 'Add driver'}</button>
      </div> : <div className="settingsDisabledNotice">Adding new driver names is currently disabled by the administrator.</div>}
      {isTerminalAdmin && <p className="muted small settingsAdminHint">As Admin you can change this under Terminal administration → Terminal settings below.</p>}
    </div>

    {me.role === 'Superuser' && <div className="card noteCard"><b>Superuser access</b><p>You can view/acknowledge warnings, manage cancellations and export data. Terminal configuration remains Admin-only.</p></div>}

    {isTerminalAdmin && <div className="terminalAdminSettingsBlock">
      <div className="settingsSectionHeading"><h2>Terminal administration · {me.terminalCode}</h2><p className="muted">Manage only your assigned terminal. You can create users up to Admin, but cannot create SuperAdmins or access another operating terminal.</p></div>
      <Admin me={me} embedded />
    </div>}
  </section>
}

function Toggle({ label, text, checked, onChange }) {
  return <label className="toggleRow"><div><b>{label}</b><span>{text}</span></div><input type="checkbox" checked={!!checked} onChange={e => onChange(e.target.checked)} /></label>
}

function Export({ me }) {
  const initial = periodDates('thisMonth')
  const [options, setOptions] = useState({ transporters: [], vehicles: [], drivers: [], palletTypes: [] })
  const [preset, setPreset] = useState('thisMonth')
  const [from, setFrom] = useState(initial.from)
  const [to, setTo] = useState(initial.to)
  const [type, setType] = useState('receipts')
  const [format, setFormat] = useState('csv')
  const [palletTypeId, setPalletTypeId] = useState('')
  const [transporterIds, setTransporterIds] = useState([])
  const [vehicleIds, setVehicleIds] = useState([])
  const [driverIds, setDriverIds] = useState([])
  const [direction, setDirection] = useState('all')
  const [status, setStatus] = useState('active')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    api('/statistics/options').then(setOptions).catch(e => setError(e.message))
  }, [])

  function changePreset(value) {
    setPreset(value)
    if (value !== 'custom') {
      const r = periodDates(value)
      setFrom(r.from)
      setTo(r.to)
    }
  }

  function quickPeriod(value) {
    const r = periodDates(value)
    setPreset(value)
    setFrom(r.from)
    setTo(r.to)
  }

  function changeType(value) {
    setType(value)
    if (value === 'complete') setFormat('xlsx')
  }

  const visibleVehicles = useMemo(() => {
    if (!transporterIds.length) return options.vehicles
    const set = new Set(transporterIds.map(Number))
    return options.vehicles.filter(v => v.transporterId && set.has(Number(v.transporterId)))
  }, [options.vehicles, transporterIds])

  async function download() {
    setError('')
    setBusy(true)
    try {
      const token = localStorage.getItem('token')
      const p = new URLSearchParams({ from, to, type, format, direction, status })
      if (palletTypeId) p.set('palletTypeId', palletTypeId)
      if (transporterIds.length) p.set('transporterIds', transporterIds.join(','))
      if (vehicleIds.length) p.set('vehicleIds', vehicleIds.join(','))
      if (driverIds.length) p.set('driverIds', driverIds.join(','))

      const res = await fetch(`${API}/export?${p}`, { headers: { Authorization: `Bearer ${token}` } })
      if (!res.ok) {
        const text = await res.text()
        let message = `Export failed (${res.status})`
        try { message = JSON.parse(text)?.message || message } catch { /* keep fallback */ }
        throw new Error(message)
      }

      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const disposition = res.headers.get('content-disposition') || ''
      const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i)
      const fallbackType = type === 'complete' ? 'CompleteReport' : type
      const filename = match ? decodeURIComponent(match[1].replace(/\"/g, '')) : `PalletControl_${me.role === 'Viewer' ? accountScopeLabel(me).replaceAll(' + ', '-').replaceAll(' ', '_') : me.terminalCode}_${fallbackType}_${from}_${to}.${format}`
      const a = document.createElement('a')
      a.href = url
      a.download = filename
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  return <section>
    <div className="pageTitle"><div><h1>Export · {accountScopeLabel(me)}</h1><p>{me.role === 'Viewer' ? `Exports are restricted to your assigned transporter(s): ${accountScopeLabel(me)}.` : `Exports are restricted to terminal ${me.terminalCode}.`} Choose a detailed CSV, a summary, a daily-check report, or a complete Excel workbook.</p></div></div>
    {error && <div className="error">{error}</div>}

    <div className="card statsFilterCard">
      <div className="segmented" style={{ marginBottom: 14, flexWrap: 'wrap' }}>
        {[
          ['today', 'Today'], ['yesterday', 'Yesterday'], ['thisWeek', 'This week'], ['previousWeek', 'Last week'],
          ['thisMonth', 'This month'], ['lastMonth', 'Last month'], ['thisYear', 'This year'], ['lastYear', 'Last year']
        ].map(([value, label]) => <button key={value} className={preset === value ? 'active' : ''} onClick={() => quickPeriod(value)}>{label}</button>)}
      </div>

      <div className="filterGrid">
        <label>Export type<select value={type} onChange={e => changeType(e.target.value)}>
          <option value="receipts">Receipt details</option>
          <option value="vehicles">Vehicle pallet summary</option>
          <option value="drivers">Driver summary</option>
          <option value="transporters">Transporter summary</option>
          <option value="daily">Daily vehicle receipt check</option>
          <option value="missing">Missing receipt report only</option>
          <option value="complete">Complete Excel report — all sheets</option>
        </select></label>
        <label>Format<select value={format} onChange={e => setFormat(e.target.value)} disabled={type === 'complete'}>
          <option value="csv">CSV</option><option value="xlsx">Excel (.xlsx)</option>
        </select></label>
        <label>Date period<select value={preset} onChange={e => changePreset(e.target.value)}>
          <option value="today">Today</option><option value="yesterday">Yesterday</option>
          <option value="thisWeek">This week</option><option value="previousWeek">Previous week</option>
          <option value="thisMonth">This month</option><option value="lastMonth">Last month</option>
          <option value="thisYear">This year</option><option value="lastYear">Last year</option><option value="custom">Custom dates</option>
        </select></label>
        <label>From<input type="date" value={from} onChange={e => { setPreset('custom'); setFrom(e.target.value) }} /></label>
        <label>To<input type="date" value={to} onChange={e => { setPreset('custom'); setTo(e.target.value) }} /></label>
        <label>Pallet type<select value={palletTypeId} onChange={e => setPalletTypeId(e.target.value)}><option value="">All pallet types</option>{options.palletTypes.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        <MultiSelect label="Transporter" options={options.transporters} selected={transporterIds} setSelected={ids => {
          setTransporterIds(ids)
          if (ids.length) {
            const allowed = new Set(options.vehicles.filter(v => ids.map(Number).includes(Number(v.transporterId))).map(v => Number(v.id)))
            setVehicleIds(old => old.filter(id => allowed.has(Number(id))))
          }
        }} labelKey={me.role === 'Viewer' ? 'label' : 'name'} />
        <MultiSelect label="Vehicle" options={visibleVehicles.map(v => ({ ...v, name: `${v.vehicleId} — ${v.transporter}` }))} selected={vehicleIds} setSelected={setVehicleIds} labelKey="name" />
        <MultiSelect label="Driver name" options={options.drivers} selected={driverIds} setSelected={setDriverIds} labelKey="name" />
        <label>Direction<select value={direction} onChange={e => setDirection(e.target.value)}><option value="all">All</option><option value="IN">IN only</option><option value="OUT">OUT only</option></select></label>
        <label>Receipt status<select value={status} onChange={e => setStatus(e.target.value)}><option value="active">Active</option><option value="cancelled">Cancelled</option><option value="all">All</option></select></label>
      </div>

      <div className="muted small" style={{ marginBottom: 12 }}>
        Driver summary includes raw balance, unmatched IN receipts, the configured deduction per unmatched IN, and adjusted balance. Daily/missing reports use active receipts and the vehicle work schedule/holiday rules.
      </div>
      <button className="primary" onClick={download} disabled={busy}>{busy ? 'Preparing export…' : `Download ${format === 'xlsx' ? 'Excel' : 'CSV'}`}</button>
    </div>
  </section>
}

function Admin({ me, embedded = false }) {
  const superAdmin = me.role === 'SuperAdmin'
  const [category,setCategory]=useState(''),[data,setData]=useState(null),[loadedCategory,setLoadedCategory]=useState(''),[loading,setLoading]=useState(false),[error,setError]=useState(''),[message,setMessage]=useState('')
  const activeCategoryRef=useRef('')
  const adminLoadSequence=useRef(0)
  const [targetTerminalId,setTargetTerminalId]=useState(String(me.terminalId))
  const [adminTerminals,setAdminTerminals]=useState(() => [{ id: me.terminalId, code: me.terminalCode, name: me.terminalCode }])
  const [transporterName,setTransporterName]=useState(''),[palletName,setPalletName]=useState(''),[linehaulComment,setLinehaulComment]=useState('')
  const [terminalForm,setTerminalForm]=useState({code:'',name:'',aliases:''})
  const [vehicleForm,setVehicleForm]=useState({vehicleId:'',terminalId:String(me.terminalId),transporterId:''})
  const [driverForm,setDriverForm]=useState({name:'',terminalId:String(me.terminalId)})
  const [userForm,setUserForm]=useState({username:'',displayName:'',password:'',role:'User',terminalId:String(me.terminalId),hasInternalPalletAccounting:true,hasLinehaul:false,hasReceivedControl:false,showDriverStatisticsTab:true,showDailyCheckTab:true,viewerTransporterIds:[]})
  const [holidayForm,setHolidayForm]=useState({date:dateInput(),name:''})

  const categories=[
    {id:'users',icon:'👤',title:'Users & access',description:'Security role, terminal and operational modules.'},
    {id:'vehicles',icon:'🚚',title:'Vehicles',description:'Vehicles for the terminal and operating days.'},
    {id:'drivers',icon:'🪪',title:'Driver names',description:'Selectable driver names for the terminal.'},
    {id:'terminalSettings',icon:'⚙️',title:'Terminal settings',description:'Warnings, driver adjustment and registration settings for one terminal.'},
    {id:'linehaulComments',icon:'💬',title:'Linehaul comments',description:'Selectable standard comments for Linehaul registration.'},
    {id:'locations',icon:'📍',title:'Linehaul / Mottatt locations',description:'Terminal-specific From/To names, display names and import aliases.'},
    {id:'transporters',icon:'🏢',title:'Transporters',description:'Transport companies belonging only to this operating terminal.'},
    ...(superAdmin?[
      {id:'pallets',icon:'📦',title:'Pallet types',description:'Global pallet type master list.'},
      {id:'holidays',icon:'📅',title:'Global holidays',description:'Non-working days applied across terminals.'},
      {id:'globalSettings',icon:'🌐',title:'Global defaults',description:'SuperAdmin-only defaults used when new terminal settings are created.'},
      {id:'database',icon:'🗄️',title:'Database & backup',description:'Database status and consistent SQLite backups.'},
      {id:'system',icon:'📈',title:'System health',description:'SuperAdmin monitoring, security, performance and telemetry graphs.'}
    ]:[])
  ]

  const showManageTerminal = superAdmin && !['pallets','holidays','globalSettings','database','system'].includes(category)

  function terminalIdForAdmin(response, requestedId) {
    const requested = Number(requestedId)
    if (response?.terminals?.some(t => Number(t.id) === requested)) return requested
    if (Number(response?.terminalId)) return Number(response.terminalId)
    return Number(me.terminalId)
  }

  function endpoint(cat, terminalId=targetTerminalId){const q=`?terminalId=${terminalId}`;return {users:'/admin/users'+q,vehicles:'/admin/vehicles'+q,drivers:'/admin/drivers'+q,terminalSettings:'/admin/terminal-settings'+q,linehaulComments:'/admin/linehaul-comments'+q,locations:'/admin/linehaul-locations'+q,transporters:'/admin/transporters'+q,pallets:'/admin/pallet-types',holidays:'/admin/holidays',globalSettings:'/admin/settings',database:'/admin/database/status',system:'/admin/system/overview'}[cat]}
  async function load(cat=activeCategoryRef.current||category, terminalId=targetTerminalId){
    if(!cat)return
    const seq=++adminLoadSequence.current
    const requestedCategory=cat
    setLoading(true);setError('')
    try{
      const url=endpoint(requestedCategory,terminalId)
      if(!url) throw new Error(`Unknown Admin category: ${requestedCategory}`)
      const r=await api(url)
      if(seq!==adminLoadSequence.current||activeCategoryRef.current!==requestedCategory)return
      setData(r);setLoadedCategory(requestedCategory)
      if(requestedCategory==='vehicles'&&r.terminals?.length)setVehicleForm(f=>{const selectedTerminalId=String(terminalIdForAdmin(r,terminalId));const transporter=r.transporters?.find(t=>t.active&&Number(t.terminalId)===Number(selectedTerminalId));return {...f,terminalId:selectedTerminalId,transporterId:String(transporter?.id||'')}})
      if(requestedCategory==='users'&&r.terminals?.length)setUserForm(f=>({...f,terminalId:String(terminalIdForAdmin(r,terminalId))}))
      if(requestedCategory==='drivers'&&r.terminals?.length)setDriverForm(f=>({...f,terminalId:String(terminalIdForAdmin(r,terminalId))}))
    }catch(e){if(seq===adminLoadSequence.current&&activeCategoryRef.current===requestedCategory){const message=requestedCategory==='system'&&e.status===404?'System Health endpoint was not found. Stop the old backend, start v5.9.1, and verify /api/version reports 5.9.1.':e.message;setError(message)}}finally{if(seq===adminLoadSequence.current&&activeCategoryRef.current===requestedCategory)setLoading(false)}
  }
  useEffect(()=>{
    let cancelled=false
    if(!superAdmin){
      setAdminTerminals([{id:me.terminalId,code:me.terminalCode,name:me.terminalCode}])
      setTargetTerminalId(String(me.terminalId))
      return ()=>{cancelled=true}
    }

    api('/admin/terminals')
      .then(r=>{
        if(cancelled)return
        const rows=r?.terminals||[]
        setAdminTerminals(rows)
        if(rows.length&&!rows.some(t=>String(t.id)===String(targetTerminalId))){
          setTargetTerminalId(String(rows.find(t=>String(t.id)===String(me.terminalId))?.id||rows[0].id))
        }
      })
      .catch(e=>{if(!cancelled)setError(e.message)})

    return()=>{cancelled=true}
  },[superAdmin,me.terminalId,me.terminalCode])

  useEffect(()=>{
    setVehicleForm(f=>({...f,terminalId:String(targetTerminalId),transporterId:''}))
    setDriverForm(f=>({...f,terminalId:String(targetTerminalId)}))
    setUserForm(f=>({...f,terminalId:String(targetTerminalId)}))
  },[targetTerminalId])

  useEffect(()=>{
    activeCategoryRef.current=category
    adminLoadSequence.current+=1
    setData(null);setLoadedCategory('');setMessage('');setError('')
    if(category)load(category,targetTerminalId);else setLoading(false)
  },[category,targetTerminalId])
  useEffect(()=>{
    if(category!=='system')return
    const timer=setInterval(()=>load('system',targetTerminalId),15000)
    return()=>clearInterval(timer)
  },[category,targetTerminalId])
  async function action(fn,ok='Saved.'){
    const actionCategory=activeCategoryRef.current
    const actionTerminal=targetTerminalId
    setError('');setMessage('')
    try{
      await fn()
      if(activeCategoryRef.current===actionCategory){await load(actionCategory,actionTerminal);if(activeCategoryRef.current===actionCategory)setMessage(ok)}
    }catch(e){if(activeCategoryRef.current===actionCategory)setError(e.message)}
  }
  function choose(id){
    activeCategoryRef.current=id
    adminLoadSequence.current+=1
    setData(null);setLoadedCategory('');setLoading(true);setError('');setMessage('')
    setCategory(id)
  }
  const activeTransporters=data?.transporters?.filter(t=>t.active && (!t.terminalId || Number(t.terminalId)===Number(vehicleForm.terminalId)))||[]
  const roleOptions=superAdmin?['User','Viewer','Superuser','Admin','SuperAdmin']:['User','Viewer','Superuser','Admin']

  function closeCategory(){activeCategoryRef.current='';adminLoadSequence.current+=1;setData(null);setLoadedCategory('');setLoading(false);setError('');setMessage('');setCategory('')}

  return <section className={embedded?'embeddedAdmin':''}>{!embedded&&<div className="pageTitle"><div><h1>{superAdmin?'SuperAdmin':'Admin'} · {me.terminalCode}</h1><p>{superAdmin?'Global administration plus every operating terminal. Choose which terminal you want to manage below.':'Administration is restricted to your assigned terminal.'}</p></div>{category&&<button onClick={closeCategory}>Close category</button>}</div>}
    {showManageTerminal&&<div className="card superAdminManageTerminal"><div><b>Manage terminal</b><p className="muted small">This controls terminal-specific Admin categories. It is separate from the active operational terminal in the top header.</p></div><select value={targetTerminalId} onChange={e=>setTargetTerminalId(e.target.value)}>{adminTerminals.map(t=><option key={t.id} value={String(t.id)}>{t.code} — {t.name}</option>)}</select></div>}
    {embedded&&category&&<div className="embeddedAdminClose"><button onClick={closeCategory}>← Back to terminal administration</button></div>}
    <div className="adminCategoryGrid">{categories.map(c=><button key={c.id} className={`adminCategoryCard ${category===c.id?'active':''}`} onClick={()=>choose(c.id)}><span className="adminCategoryIcon">{c.icon}</span><span className="adminCategoryText"><b>{c.title}</b><small>{c.description}</small></span><span className="adminCategoryArrow">›</span></button>)}</div>
    {message&&<div className="success adminFeedback">{message}</div>}{error&&<div className="error adminFeedback">{error}</div>}
    {!category&&<div className="card adminWelcome"><b>Select an Admin category above</b><p className="muted">Admin cannot modify global configuration or another operating terminal.</p></div>}{category&&loading&&<Loading/>}
    {category&&!loading&&data&&loadedCategory===category&&<div key={category} className="adminCategoryContent">
      {category==='locations'&&<AdminSection title={`Linehaul / Mottatt locations · ${data.terminalCode}`} subtitle="These names are only routing/statistics locations for the selected operating terminal. SRD, ARE and KRS are the only real PalletControl terminals. Code, Name and Aliases are all accepted on import.">{superAdmin&&<label className="adminTerminalPicker">Operating terminal<select value={targetTerminalId} onChange={e=>setTargetTerminalId(e.target.value)}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>}<div className="inlineForm terminalCreate"><input placeholder="Location code, e.g. AL" value={terminalForm.code} onChange={e=>setTerminalForm({...terminalForm,code:e.target.value.toUpperCase()})}/><input placeholder="Display name" value={terminalForm.name} onChange={e=>setTerminalForm({...terminalForm,name:e.target.value})}/><input placeholder="Import aliases, e.g. AL123; Arendal Lager" value={terminalForm.aliases} onChange={e=>setTerminalForm({...terminalForm,aliases:e.target.value})}/><button className="primary" onClick={()=>action(async()=>{await api('/admin/linehaul-locations',{method:'POST',body:JSON.stringify({terminalId:Number(targetTerminalId),...terminalForm})});setTerminalForm({code:'',name:'',aliases:''})},'Location added.')}>Add location</button></div><div className="adminRows terminalAdminRows">{data.locations.map(t=><TerminalAdminRow key={t.id} row={t} save={next=>action(()=>api(`/admin/linehaul-locations/${t.id}`,{method:'PUT',body:JSON.stringify(next)}),`Location ${next.code} updated.`)} remove={()=>{if(window.confirm(`Delete location ${t.code}? Historical Linehaul/Mottatt receipts keep their saved name.`))action(()=>api(`/admin/linehaul-locations/${t.id}`,{method:'DELETE'}),'Location deleted.')}}/>)}</div></AdminSection>}
      {category==='transporters'&&<AdminSection title={`Transporters · ${data.terminalCode}`} subtitle="Transporters are terminal-specific. Changes here affect only this operating terminal.">{superAdmin&&<label className="adminTerminalPicker">Operating terminal<select value={targetTerminalId} onChange={e=>setTargetTerminalId(e.target.value)}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>}<div className="inlineForm"><input placeholder="Transporter name" value={transporterName} onChange={e=>setTransporterName(e.target.value)}/><button className="primary" onClick={()=>action(async()=>{await api('/admin/transporters',{method:'POST',body:JSON.stringify({name:transporterName,terminalId:Number(targetTerminalId)})});setTransporterName('')},'Transporter added.')}>Add transporter</button></div><div className="adminRows">{data.transporters.map(t=><div className="adminRow" key={t.id}><b>{t.name}</b><span>{t.active?'Active':'Inactive'}</span><button className="dangerGhost" onClick={()=>{if(window.confirm(`Delete ${t.name} from ${data.terminalCode}? Vehicles using it will become unassigned.`))action(()=>api(`/admin/transporters/${t.id}`,{method:'DELETE'}),'Transporter deleted.')}}>Delete</button></div>)}</div></AdminSection>}
      {category==='pallets'&&<AdminSection title="Global pallet types"><div className="inlineForm"><input placeholder="New pallet type" value={palletName} onChange={e=>setPalletName(e.target.value)}/><button className="primary" onClick={()=>action(async()=>{await api('/admin/pallet-types',{method:'POST',body:JSON.stringify({name:palletName,userSelectable:true})});setPalletName('')},'Pallet type added.')}>Add pallet type</button></div><div className="adminRows">{data.palletTypes.map(p=><PalletAdminRow key={p.id} row={p} save={next=>action(()=>api(`/admin/pallet-types/${p.id}`,{method:'PUT',body:JSON.stringify(next)}))}/>)}</div></AdminSection>}
      {category==='holidays'&&<AdminSection title="Global holidays / non-working days" subtitle="These remain global and can only be changed by SuperAdmin."><div className="inlineForm three"><input type="date" value={holidayForm.date} onChange={e=>setHolidayForm({...holidayForm,date:e.target.value})}/><input placeholder="Name" value={holidayForm.name} onChange={e=>setHolidayForm({...holidayForm,name:e.target.value})}/><button className="primary" onClick={()=>action(async()=>{await api('/admin/holidays',{method:'POST',body:JSON.stringify(holidayForm)});setHolidayForm({date:dateInput(),name:''})},'Holiday added.')}>Add</button></div><div className="adminRows">{data.holidays.map(h=><div className="adminRow" key={h.id}><b>{formatDate(h.date)}</b><span>{h.name}</span><button className="dangerGhost" onClick={()=>action(()=>api(`/admin/holidays/${h.id}`,{method:'DELETE'}),'Holiday removed.')}>Delete</button></div>)}</div></AdminSection>}
      {category==='vehicles'&&<AdminSection title={`Vehicles · ${data.terminalCode||me.terminalCode}`} subtitle={superAdmin?'SuperAdmin can assign vehicles to any operating terminal. Admin only sees and changes their own terminal.':'Only vehicles belonging to your terminal are shown.'}><div className="inlineForm three"><input placeholder="Vehicle ID" value={vehicleForm.vehicleId} onChange={e=>setVehicleForm({...vehicleForm,vehicleId:e.target.value.toUpperCase()})}/><select value={vehicleForm.terminalId} onChange={e=>setVehicleForm({...vehicleForm,terminalId:e.target.value,transporterId:''})}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code}</option>)}</select><select value={vehicleForm.transporterId} onChange={e=>setVehicleForm({...vehicleForm,transporterId:e.target.value})}><option value="">Choose transporter</option>{activeTransporters.map(t=><option key={t.id} value={t.id}>{t.name}</option>)}</select><button className="primary" onClick={()=>action(async()=>{await api('/admin/vehicles',{method:'POST',body:JSON.stringify({vehicleId:vehicleForm.vehicleId,terminalId:Number(vehicleForm.terminalId),transporterId:Number(vehicleForm.transporterId)})});setVehicleForm({...vehicleForm,vehicleId:''})},'Vehicle added.')}>Add vehicle</button></div><div className="adminRows">{data.vehicles.map(v=><VehicleAdminRow key={v.id} row={v} transporters={data.transporters.filter(t=>!t.terminalId||Number(t.terminalId)===Number(v.terminalId))} saveTransporter={transporterId=>action(()=>api(`/admin/vehicles/${v.id}/transporter`,{method:'PUT',body:JSON.stringify({transporterId})}),'Transporter changed.')} saveSchedule={days=>action(()=>api(`/admin/vehicles/${v.id}/schedule`,{method:'PUT',body:JSON.stringify({days})}),'Operating days saved.')} remove={()=>{if(window.confirm(`Delete ${v.vehicleId}? Historical receipts keep snapshots.`))action(()=>api(`/admin/vehicles/${v.id}`,{method:'DELETE'}),'Vehicle deleted.')}}/>)}</div></AdminSection>}
      {category==='drivers'&&<AdminSection title="Driver names" subtitle="Remove only hides a name from future registration; historical statistics remain."><div className="inlineForm"><input placeholder="Driver name" value={driverForm.name} onChange={e=>setDriverForm({...driverForm,name:e.target.value})}/><select value={driverForm.terminalId} onChange={e=>setDriverForm({...driverForm,terminalId:e.target.value})}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code}</option>)}</select><button className="primary" onClick={()=>action(async()=>{await api('/admin/drivers',{method:'POST',body:JSON.stringify({name:driverForm.name,terminalId:Number(driverForm.terminalId)})});setDriverForm({...driverForm,name:''})},'Driver added/restored.')}>Add driver</button></div><div className="adminRows">{data.drivers.map(d=><div className="adminRow driverAdmin" key={d.id}><b>{d.name}</b><span>{d.terminal}</span><span>{d.active?'Active':'Removed'}</span>{d.active?<button className="dangerGhost" onClick={()=>{if(window.confirm(`Remove ${d.name} from future selection? Historical receipts stay.`))action(()=>api(`/admin/drivers/${d.id}`,{method:'DELETE'}),'Driver removed.')}}>Remove</button>:<button onClick={()=>action(()=>api(`/admin/drivers/${d.id}/active`,{method:'PUT',body:JSON.stringify({active:true})}),'Driver restored.')}>Restore</button>}</div>)}</div></AdminSection>}
      {category==='users'&&<AdminSection title="Users & module access" subtitle={superAdmin ? 'SuperAdmin can assign a Viewer to one or more transporters across SRD, ARE and KRS. Other roles keep normal terminal/module access.' : 'Admin can manage users in this terminal. Viewer assignments outside this terminal are protected and can only be changed by SuperAdmin.'}>
        <div className="userCreateCard">
          <div className="inlineForm userAdd">
            <input placeholder="Username" value={userForm.username} onChange={e=>setUserForm({...userForm,username:e.target.value})}/>
            <input placeholder="Display name" value={userForm.displayName} onChange={e=>setUserForm({...userForm,displayName:e.target.value})}/>
            <input type="password" placeholder="Password" value={userForm.password} onChange={e=>setUserForm({...userForm,password:e.target.value})}/>
            <select value={userForm.role} onChange={e=>{
              const role=e.target.value
              setUserForm({...userForm,role,
                ...(role==='Viewer'?{hasInternalPalletAccounting:true,hasLinehaul:false,hasReceivedControl:false,showDriverStatisticsTab:true,showDailyCheckTab:true}:{}),
                viewerTransporterIds: role==='Viewer' ? userForm.viewerTransporterIds : []
              })
            }}>{roleOptions.map(r=><option key={r}>{r}</option>)}</select>
            <select value={userForm.terminalId} onChange={e=>setUserForm({...userForm,terminalId:e.target.value})}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code}</option>)}</select>
          </div>
          {userForm.role==='Viewer'
              ? <ViewerTransporterPicker transporters={data.viewerTransporters||[]} selected={userForm.viewerTransporterIds||[]} setSelected={viewerTransporterIds=>setUserForm({...userForm,viewerTransporterIds})} superAdmin={superAdmin}/>
              : <ModuleChecks value={userForm} setValue={setUserForm}/>
          }
          <button className="primary" onClick={()=>action(async()=>{
            await api('/admin/users',{method:'POST',body:JSON.stringify({...userForm,terminalId:Number(userForm.terminalId)})})
            setUserForm({...userForm,username:'',displayName:'',password:'',role:'User',viewerTransporterIds:[],hasInternalPalletAccounting:true,hasLinehaul:false,hasReceivedControl:false,showDriverStatisticsTab:true,showDailyCheckTab:true})
          },'User created.')}>Create user</button>
        </div>
        <div className="adminRows">{data.users.map(u=><UserAdminRow key={u.id} row={u} terminals={data.terminals} roles={roleOptions} viewerTransporters={data.viewerTransporters||[]} superAdmin={superAdmin} save={next=>action(()=>api(`/admin/users/${u.id}`,{method:'PUT',body:JSON.stringify(next)}),'User updated.')} resetPassword={()=>{const password=window.prompt(`New password for ${u.username}:`);if(password)action(()=>api(`/admin/users/${u.id}/password`,{method:'POST',body:JSON.stringify({password})}),'Password changed.')}}/>)}</div>
      </AdminSection>}
      {category==='terminalSettings'&&<AdminSettingsScope title="Terminal settings" data={data} targetTerminalId={targetTerminalId} setTargetTerminalId={setTargetTerminalId} showTerminalSelect={superAdmin} save={next=>action(()=>api(`/admin/terminal-settings?terminalId=${targetTerminalId}`,{method:'PUT',body:JSON.stringify(next)}),'Terminal settings saved.')}/>}
      {category==='globalSettings'&&<AdminOperationalSettings title="Global defaults" subtitle="SuperAdmin only. These values are copied when a new terminal gets its own settings; existing terminal settings remain independent." settings={data} save={next=>action(()=>api('/admin/settings',{method:'PUT',body:JSON.stringify(next)}),'Global defaults saved.')}/>}
      {category==='linehaulComments'&&<AdminSection title={`Linehaul selectable comments · ${data.terminalCode}`} subtitle="These texts belong to the selected user terminal and appear regardless of which From/To terminal is chosen on Linehaul registration.">{superAdmin&&<label className="adminTerminalPicker">Terminal<select value={targetTerminalId} onChange={e=>setTargetTerminalId(e.target.value)}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>}<div className="inlineForm"><input placeholder="Selectable comment" value={linehaulComment} onChange={e=>setLinehaulComment(e.target.value)}/><button className="primary" onClick={()=>action(async()=>{await api('/admin/linehaul-comments',{method:'POST',body:JSON.stringify({terminalId:Number(targetTerminalId),text:linehaulComment})});setLinehaulComment('')},'Linehaul comment added.')}>Add comment</button></div><div className="adminRows">{data.comments.map(c=><div className="adminRow" key={c.id}><b>{c.text}</b><span>{c.active?'Active':'Inactive'}</span><button onClick={()=>action(()=>api(`/admin/linehaul-comments/${c.id}/active`,{method:'PUT',body:JSON.stringify({active:!c.active})}),c.active?'Comment disabled.':'Comment enabled.')}>{c.active?'Disable':'Enable'}</button></div>)}</div></AdminSection>}
      {category==='system'&&<SystemMonitoring data={data} refresh={()=>load('system',targetTerminalId)} createBackup={()=>action(()=>api('/admin/system/backup',{method:'POST',body:'{}'}),'Database backup created.')}/>}
      {category==='database'&&<AdminSection title="Database & backup" subtitle="All modules use this same SQLite database, so Linehaul and MottattKontroll are included in every backup."><div className="detailGrid"><span><small>Database file</small><b>{data.databasePath}</b></span><span><small>Database size</small>{Math.round(Number(data.databaseSizeBytes||0)/1024)} KB</span><span><small>Backup folder</small><b>{data.backupDirectory}</b></span><span><small>Automatic backup</small>Every {data.backupIntervalHours} hour(s)</span><span><small>Retention</small>{data.backupRetentionDays} days</span><span><small>Backup count</small>{data.backupCount}</span><span><small>Latest backup</small>{data.latestBackupUtc?formatTimestamp(data.latestBackupUtc):'No backup yet'}</span></div><button className="primary" onClick={()=>action(()=>api('/admin/database/backup',{method:'POST',body:'{}'}),'Database backup created.')}>Create backup now</button></AdminSection>}
    </div>}
  </section>
}

function bytes(value) {
  const n = Number(value || 0)
  if (!Number.isFinite(n) || n <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(units.length - 1, Math.floor(Math.log(n) / Math.log(1024)))
  return `${(n / Math.pow(1024, index)).toFixed(index >= 3 ? 1 : 0)} ${units[index]}`
}

function uptimeText(seconds) {
  const total = Math.max(0, Math.floor(Number(seconds || 0)))
  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return `${days ? `${days}d ` : ''}${hours}h ${minutes}m`
}

function MiniLineGraph({ title, samples, field, suffix = '', transform = v => Number(v || 0) }) {
  const values = (samples || []).map(x => transform(x[field])).filter(Number.isFinite)
  const max = Math.max(1, ...values)
  const min = Math.min(0, ...values)
  const range = Math.max(1, max - min)
  const points = values.map((value, index) => {
    const x = values.length <= 1 ? 0 : index / (values.length - 1) * 100
    const y = 36 - ((value - min) / range * 32)
    return `${x.toFixed(2)},${y.toFixed(2)}`
  }).join(' ')
  const latest = values.length ? values[values.length - 1] : 0
  return <div className="systemGraph card">
    <div className="systemGraphHead"><div><b>{title}</b><small>Rolling history</small></div><strong>{latest.toFixed(latest >= 100 ? 0 : 1)}{suffix}</strong></div>
    <svg viewBox="0 0 100 40" preserveAspectRatio="none" aria-label={title}><polyline points={points || '0,36 100,36'} fill="none" vectorEffect="non-scaling-stroke" /></svg>
  </div>
}

function SystemMonitoring({ data, refresh, createBackup }) {
  const t = data.telemetry || {}
  const history = t.history || []
  const diskPercent = data.disk?.totalBytes ? Math.round((Number(data.disk.freeBytes || 0) / Number(data.disk.totalBytes)) * 100) : null
  return <div className="systemMonitoring">
    <div className="systemMonitoringTop">
      <div><h2>System health · v{data.version}</h2><p className="muted">Live server performance, application security and OpenTelemetry status. Refreshes automatically every 15 seconds.</p></div>
      <div className="systemMonitoringActions"><button onClick={refresh}>Refresh</button><button className="primary" onClick={createBackup}>Backup now</button></div>
    </div>
    <div className="systemMetricGrid">
      <SystemMetric label="Uptime" value={uptimeText(t.uptimeSeconds)} />
      <SystemMetric label="CPU" value={`${Number(t.cpuPercent || 0).toFixed(1)}%`} />
      <SystemMetric label="Process RAM" value={bytes(t.processMemoryBytes)} />
      <SystemMetric label="Requests/min" value={t.requestsLastMinute || 0} />
      <SystemMetric label="Avg response" value={`${Number(t.averageResponseMs || 0).toFixed(1)} ms`} />
      <SystemMetric label="Active users · 15m" value={t.activeUsersLast15Minutes || 0} />
      <SystemMetric label="HTTP 5xx" value={t.http5xx || 0} tone={Number(t.http5xx || 0) > 0 ? 'bad' : 'good'} />
      <SystemMetric label="Disk free" value={diskPercent == null ? 'Unavailable' : `${diskPercent}% · ${bytes(data.disk.freeBytes)}`} tone={diskPercent != null && diskPercent < 15 ? 'bad' : 'good'} />
    </div>
    <div className="systemGraphs">
      <MiniLineGraph title="CPU usage" samples={history} field="cpuPercent" suffix="%" />
      <MiniLineGraph title="Process RAM" samples={history} field="processMemoryBytes" transform={v => Number(v || 0) / 1024 / 1024} suffix=" MB" />
      <MiniLineGraph title="Requests / minute" samples={history} field="requestsPerMinute" />
      <MiniLineGraph title="Average API response" samples={history} field="averageResponseMs" suffix=" ms" />
    </div>
    <div className="systemDetailColumns">
      <AdminSection title="Security status" subtitle="Configuration only. Secrets and passwords are never returned to the browser.">
        <div className="detailGrid systemDetailGrid">
          <span><small>HTTPS required</small><b>{data.security?.requireHttps ? 'Yes' : 'No'}</b></span>
          <span><small>JWT lifetime</small><b>{data.security?.jwtLifetimeMinutes} min</b></span>
          <span><small>API rate limit</small><b>{data.security?.apiRequestsPerMinute}/min/IP</b></span>
          <span><small>Login rate limit</small><b>{data.security?.loginRequestsPerMinute}/min/IP</b></span>
          <span><small>Failed login lockout</small><b>{data.security?.loginFailureLimit} attempts / {data.security?.loginLockoutMinutes} min</b></span>
          <span><small>Max request body</small><b>{data.security?.maxRequestBodyMb} MB</b></span>
          <span><small>401 Unauthorized</small><b>{t.unauthorized401 || 0}</b></span>
          <span><small>403 Forbidden</small><b>{t.forbidden403 || 0}</b></span>
          <span><small>429 Rate limited</small><b>{t.rateLimited429 || 0}</b></span>
        </div>
      </AdminSection>
      <AdminSection title="Database & deployment" subtitle="Production database and backups should stay outside the GitHub checkout and IIS publish folder.">
        <div className="detailGrid systemDetailGrid">
          <span><small>Database</small><b className="pathValue">{data.database?.path}</b></span>
          <span><small>SQLite quick check</small><b>{data.database?.quickCheck}</b></span>
          <span><small>Database size</small><b>{bytes(data.database?.sizeBytes)}</b></span>
          <span><small>Backup folder</small><b className="pathValue">{data.backup?.directory}</b></span>
          <span><small>Latest backup</small><b>{data.backup?.latestBackupUtc ? formatTimestamp(data.backup.latestBackupUtc) : 'No backup yet'}</b></span>
          <span><small>Backups</small><b>{data.backup?.count || 0} · {data.backup?.retentionDays} day retention</b></span>
          <span><small>Environment</small><b>{data.environment}</b></span>
          <span><small>OpenTelemetry</small><b>{data.openTelemetry?.enabled ? 'OTLP configured' : 'Local monitoring only'}</b></span>
        </div>
      </AdminSection>
    </div>
    <AdminSection title="Endpoint performance" subtitle="Most-used API routes since the backend started.">
      <div className="systemEndpointTable"><div className="systemEndpointHeader"><span>Endpoint</span><span>Requests</span><span>Average</span><span>Errors</span><span>Last status</span></div>{(t.endpoints || []).map(row => <div className="systemEndpointRow" key={row.path}><code>{row.path}</code><span>{row.requests}</span><span>{Number(row.averageMs || 0).toFixed(1)} ms</span><span>{row.errors}</span><span>{row.lastStatusCode}</span></div>)}{!(t.endpoints || []).length && <div className="empty">No API request data yet.</div>}</div>
    </AdminSection>
    <AdminSection title="Recent server errors" subtitle="Recent exception messages and paths are kept only in memory and clear when the backend restarts.">
      <div className="systemErrorList">{(t.recentErrors || []).map((e, index) => <div className="systemErrorRow" key={`${e.timestampUtc}-${index}`}><span>{formatTimestamp(e.timestampUtc)}</span><code>{e.path}</code><b>{e.message}</b></div>)}{!(t.recentErrors || []).length && <div className="success">No server exceptions recorded since startup.</div>}</div>
    </AdminSection>
  </div>
}

function SystemMetric({ label, value, tone = '' }) {
  return <div className={`systemMetric ${tone}`}><small>{label}</small><strong>{value}</strong></div>
}

function TerminalAdminRow({ row, save, remove }) {
  const [v,setV]=useState({code:row.code||'',name:row.name||'',aliases:row.aliases||'',active:row.active!==false})
  useEffect(()=>setV({code:row.code||'',name:row.name||'',aliases:row.aliases||'',active:row.active!==false}),[row])
  return <div className="adminRow terminalAdminRow">
    <label><small>Code shown in statistics</small><input value={v.code} onChange={e=>setV({...v,code:e.target.value.toUpperCase()})}/></label>
    <label><small>Display name</small><input value={v.name} onChange={e=>setV({...v,name:e.target.value})}/></label>
    <label><small>Import aliases</small><input value={v.aliases} onChange={e=>setV({...v,aliases:e.target.value})} placeholder="AL123; Arendal Lager"/></label>
    <label className="miniCheck"><input type="checkbox" checked={v.active} onChange={e=>setV({...v,active:e.target.checked})}/> Active</label>
    <button onClick={()=>save(v)}>Save</button>
    {remove&&<button className="dangerGhost" onClick={remove}>Delete</button>}
  </div>
}

function ModuleChecks({ value, setValue }) {
  return <div className="moduleChecks"><label className="miniCheck"><input type="checkbox" checked={!!value.hasInternalPalletAccounting} onChange={e=>setValue(v=>({...v,hasInternalPalletAccounting:e.target.checked}))}/> InternPalleregnskap</label><label className="miniCheck"><input type="checkbox" checked={!!value.hasLinehaul} onChange={e=>setValue(v=>({...v,hasLinehaul:e.target.checked}))}/> Linehaul</label><label className="miniCheck"><input type="checkbox" checked={!!value.hasReceivedControl} onChange={e=>setValue(v=>({...v,hasReceivedControl:e.target.checked}))}/> MottattKontroll</label></div>
}

function AdminSettingsScope({ title, data, targetTerminalId, setTargetTerminalId, showTerminalSelect, save }) {
  return <div>{showTerminalSelect&&<label className="adminTerminalPicker">Terminal<select value={targetTerminalId} onChange={e=>setTargetTerminalId(e.target.value)}>{data.terminals.map(t=><option key={t.id} value={t.id}>{t.code} — {t.name}</option>)}</select></label>}<AdminOperationalSettings title={`${title} · ${data.terminalCode}`} subtitle="These values apply only to this terminal." settings={data.settings} save={save}/></div>
}

function AdminOperationalSettings({ title, subtitle, settings, save }) {
  const [s,setS]=useState({...settings})
  useEffect(()=>setS({...settings}),[settings])
  const set=(k,v)=>setS(old=>({...old,[k]:v}))
  return <div className="card adminSection"><h2>{title}</h2><p className="muted">{subtitle}</p><div className="settingsGroups adminSettingsTwoCol">
    <SettingsGroup title="Submission warnings"><Rule label="Large IN" enabled={s.largeInEnabled} setEnabled={v=>set('largeInEnabled',v)} value={s.largeInThreshold} setValue={v=>set('largeInThreshold',v)} suffix="pallets"/><Rule label="Large OUT" enabled={s.largeOutEnabled} setEnabled={v=>set('largeOutEnabled',v)} value={s.largeOutThreshold} setValue={v=>set('largeOutThreshold',v)} suffix="pallets"/><Rule label="Recent vehicle submission" enabled={s.recentVehicleEnabled} setEnabled={v=>set('recentVehicleEnabled',v)} value={s.recentVehicleMinutes} setValue={v=>set('recentVehicleMinutes',v)} suffix="minutes"/><Rule label="Recent driver submission" enabled={s.recentDriverEnabled} setEnabled={v=>set('recentDriverEnabled',v)} value={s.recentDriverMinutes} setValue={v=>set('recentDriverMinutes',v)} suffix="minutes"/><Rule label="Possible exact duplicate" enabled={s.duplicateEnabled} setEnabled={v=>set('duplicateEnabled',v)} value={s.duplicateMinutes} setValue={v=>set('duplicateMinutes',v)} suffix="minutes"/><Rule label="Rapid submissions" enabled={s.rapidSubmissionsEnabled} setEnabled={v=>set('rapidSubmissionsEnabled',v)} value={s.rapidSubmissionCount} setValue={v=>set('rapidSubmissionCount',v)} suffix="submissions"/><Rule label="High vehicle daily total" enabled={s.dailyTotalEnabled} setEnabled={v=>set('dailyTotalEnabled',v)} value={s.dailyTotalThreshold} setValue={v=>set('dailyTotalThreshold',v)} suffix="pallets"/></SettingsGroup>
    <SettingsGroup title="Receipt & driver rules"><SimpleRule label="Warning when receipt is cancelled" enabled={s.cancellationWarningEnabled} setEnabled={v=>set('cancellationWarningEnabled',v)}/><SimpleRule label="Warning when cancellation is reversed" enabled={s.cancellationReversedWarningEnabled} setEnabled={v=>set('cancellationReversedWarningEnabled',v)}/><SimpleRule label="Allow users to add driver names from Settings" enabled={s.allowUsersAddDrivers} setEnabled={v=>set('allowUsersAddDrivers',v)}/><div className="ruleRow"><label><b>Deduction per unmatched driver IN receipt</b></label><input className="smallNumber" type="number" min="0" max="5000" value={s.driverUnmatchedInDeduction??15} onChange={e=>set('driverUnmatchedInDeduction',Number(e.target.value))}/><span>pallets</span></div></SettingsGroup>
    <SettingsGroup title="Submit notifications"><Rule label="Monthly milestones" enabled={s.milestoneNotificationsEnabled} setEnabled={v=>set('milestoneNotificationsEnabled',v)} value={s.monthlyMilestoneStep} setValue={v=>set('monthlyMilestoneStep',v)} suffix="pallet step"/><SimpleRule label="Leaderboard messages" enabled={s.leaderboardNotificationsEnabled} setEnabled={v=>set('leaderboardNotificationsEnabled',v)}/><SimpleRule label="Current monthly balance" enabled={s.balanceNotificationsEnabled} setEnabled={v=>set('balanceNotificationsEnabled',v)}/></SettingsGroup>
  </div><button className="primary" onClick={()=>save(s)}>Save settings</button></div>
}

function VehicleAdminRow({ row, transporters, saveTransporter, saveSchedule, remove }) {
  const [days, setDays] = useState(Array.isArray(row.operatingDays) ? row.operatingDays.map(Number) : [1, 2, 3, 4, 5])
  const weekdayNames = [[1, 'Mon'], [2, 'Tue'], [3, 'Wed'], [4, 'Thu'], [5, 'Fri'], [6, 'Sat'], [7, 'Sun']]

  function toggleDay(day) {
    setDays(current => current.includes(day) ? current.filter(x => x !== day) : [...current, day].sort((a, b) => a - b))
  }

  return <div className="adminRow vehicleAdmin" style={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
    <b>{row.vehicleId}</b>
    <span>{row.terminal}</span>
    <select value={row.transporterId || ''} onChange={e => { if (e.target.value) saveTransporter(Number(e.target.value)) }}>
      <option value="">Not assigned</option>{transporters.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
    </select>
    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
      {weekdayNames.map(([day, name]) => <label className="miniCheck" key={day}><input type="checkbox" checked={days.includes(day)} onChange={() => toggleDay(day)} /> {name}</label>)}
      <button onClick={() => saveSchedule(days)}>Save days</button>
      <span className="muted small">{days.length === 0 ? 'Never expected' : 'Expected on selected days'}</span>
    </div>
    <button className="dangerGhost" onClick={remove}>Delete</button>
  </div>
}

function AdminSection({ title, subtitle, children }) { return <div className="card adminSection"><h2>{title}</h2>{subtitle && <p className="muted">{subtitle}</p>}{children}</div> }

function PalletAdminRow({ row, save }) {
  const [active, setActive] = useState(row.active)
  const [selectable, setSelectable] = useState(row.userSelectable)
  return <div className="adminRow"><b>{row.name}</b><label className="miniCheck"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} /> Active</label><label className="miniCheck"><input type="checkbox" checked={selectable} onChange={e => setSelectable(e.target.checked)} /> User selectable</label><button onClick={() => save({ active, userSelectable: selectable })}>Save</button></div>
}

function ViewerTransporterPicker({ transporters, selected, setSelected, externalCount = 0, superAdmin = false }) {
  const selectedIds = (selected || []).map(Number)
  const visibleIds = new Set((transporters || []).map(t => Number(t.id)))
  const hiddenSelected = selectedIds.filter(id => !visibleIds.has(id))

  function toggle(id, checked) {
    const idNumber = Number(id)
    const visibleSelected = selectedIds.filter(x => visibleIds.has(x) && x !== idNumber)
    const nextVisible = checked ? [...visibleSelected, idNumber] : visibleSelected
    setSelected([...hiddenSelected, ...nextVisible])
  }

  return <div className="viewerScopePicker">
    <div className="viewerScopeHead">
      <div><b>Viewer transporter access</b><p className="muted small">{superAdmin ? 'Select one or more transporters. You may combine transporters from different terminals on the same Viewer account.' : 'Select transporters from your terminal. Existing links to other terminals are protected and can only be changed by SuperAdmin.'}</p></div>
      {externalCount > 0 && <span className="badge blue">{externalCount} cross-terminal link{externalCount === 1 ? '' : 's'} protected</span>}
    </div>
    <div className="viewerScopeOptions">
      {(transporters || []).map(t => <label className="viewerScopeOption" key={t.id}>
        <input type="checkbox" checked={selectedIds.includes(Number(t.id))} onChange={e => toggle(t.id, e.target.checked)} />
        <span><b>{t.label || t.name}</b>{t.terminalCode && <small>{t.terminalCode}</small>}</span>
      </label>)}
      {(transporters || []).length === 0 && <span className="muted">No active transporters available.</span>}
    </div>
  </div>
}

function UserAdminRow({ row, terminals, roles, viewerTransporters, superAdmin, save, resetPassword }) {
  const makeValue = source => ({
    displayName: source.displayName,
    role: source.role,
    terminalId: String(source.terminalId),
    active: source.active,
    hasInternalPalletAccounting: source.hasInternalPalletAccounting !== false,
    hasLinehaul: !!source.hasLinehaul,
    hasReceivedControl: !!source.hasReceivedControl,
    showDriverStatisticsTab: source.showDriverStatisticsTab !== false,
    showDailyCheckTab: source.showDailyCheckTab !== false,
    viewerTransporterIds: source.viewerTransporterIds || []
  })
  const [v,setV]=useState(()=>makeValue(row))
  useEffect(()=>setV(makeValue(row)),[row])

  function changeRole(role) {
    setV(current => ({
      ...current,
      role,
      ...(role === 'Viewer' ? {
        hasInternalPalletAccounting: true,
        hasLinehaul: false,
        hasReceivedControl: false,
        showDriverStatisticsTab: true,
        showDailyCheckTab: true
      } : { viewerTransporterIds: [] })
    }))
  }

  return <div className="adminRow userAdmin userAdminExpanded">
    <div className="userIdentity"><b>{row.username}</b><small>{row.terminal}</small></div>
    <input value={v.displayName} onChange={e=>setV({...v,displayName:e.target.value})}/>
    <select value={v.role} onChange={e=>changeRole(e.target.value)}>{roles.map(r=><option key={r}>{r}</option>)}</select>
    <select value={v.terminalId} onChange={e=>setV({...v,terminalId:e.target.value})}>{terminals.map(t=><option key={t.id} value={t.id}>{t.code}</option>)}</select>
    <label className="miniCheck"><input type="checkbox" checked={v.active} onChange={e=>setV({...v,active:e.target.checked})}/> Active</label>
    {v.role === 'Viewer'
        ? <ViewerTransporterPicker transporters={viewerTransporters} selected={v.viewerTransporterIds} setSelected={viewerTransporterIds=>setV({...v,viewerTransporterIds})} externalCount={row.viewerExternalTransporterCount||0} superAdmin={superAdmin}/>
        : <>
          <ModuleChecks value={v} setValue={setV}/>
          {v.hasInternalPalletAccounting&&<div className="moduleChecks subAccess"><label className="miniCheck"><input type="checkbox" checked={v.showDriverStatisticsTab} onChange={e=>setV({...v,showDriverStatisticsTab:e.target.checked})}/> Driver statistics</label><label className="miniCheck"><input type="checkbox" checked={v.showDailyCheckTab} onChange={e=>setV({...v,showDailyCheckTab:e.target.checked})}/> Daily Check</label></div>}
        </>}
    <div className="userAdminActions"><button onClick={()=>save({...v,terminalId:Number(v.terminalId)})}>Save</button><button onClick={resetPassword}>Password</button></div>
  </div>
}

function AdminWarningSettings({ settings, save }) {
  const [s, setS] = useState({ ...settings })
  const set = (key, value) => setS(old => ({ ...old, [key]: value }))

  return <div className="card adminSection">
    <h2>Warning rules</h2>
    <p className="muted">Only Admin can change these rules. Superusers can still see and acknowledge triggered warnings.</p>
    <div className="settingsGroups adminSettingsTwoCol">
      <SettingsGroup title="Submission warnings">
        <Rule label="Large IN" enabled={s.largeInEnabled} setEnabled={v => set('largeInEnabled', v)} value={s.largeInThreshold} setValue={v => set('largeInThreshold', v)} suffix="pallets" />
        <Rule label="Large OUT" enabled={s.largeOutEnabled} setEnabled={v => set('largeOutEnabled', v)} value={s.largeOutThreshold} setValue={v => set('largeOutThreshold', v)} suffix="pallets" />
        <Rule label="Recent vehicle submission" enabled={s.recentVehicleEnabled} setEnabled={v => set('recentVehicleEnabled', v)} value={s.recentVehicleMinutes} setValue={v => set('recentVehicleMinutes', v)} suffix="minutes" />
        <Rule label="Recent driver submission" enabled={s.recentDriverEnabled} setEnabled={v => set('recentDriverEnabled', v)} value={s.recentDriverMinutes} setValue={v => set('recentDriverMinutes', v)} suffix="minutes" />
        <Rule label="Possible exact duplicate" enabled={s.duplicateEnabled} setEnabled={v => set('duplicateEnabled', v)} value={s.duplicateMinutes} setValue={v => set('duplicateMinutes', v)} suffix="minutes" />
        <Rule label="Rapid submissions" enabled={s.rapidSubmissionsEnabled} setEnabled={v => set('rapidSubmissionsEnabled', v)} value={s.rapidSubmissionCount} setValue={v => set('rapidSubmissionCount', v)} suffix={`submissions / ${s.rapidSubmissionMinutes} min`} extra={<input className="smallNumber" type="number" min="1" value={s.rapidSubmissionMinutes} onChange={e => set('rapidSubmissionMinutes', Number(e.target.value))} />} />
        <Rule label="High vehicle daily total" enabled={s.dailyTotalEnabled} setEnabled={v => set('dailyTotalEnabled', v)} value={s.dailyTotalThreshold} setValue={v => set('dailyTotalThreshold', v)} suffix="pallets" />
      </SettingsGroup>

      <SettingsGroup title="Receipt event warnings">
        <SimpleRule label="Warn when a receipt is cancelled" enabled={s.cancellationWarningEnabled} setEnabled={v => set('cancellationWarningEnabled', v)} />
        <SimpleRule label="Warn when cancellation is reversed" enabled={s.cancellationReversedWarningEnabled} setEnabled={v => set('cancellationReversedWarningEnabled', v)} />
      </SettingsGroup>
    </div>
    <button className="primary" onClick={() => save(s)}>Save warning rules</button>
  </div>
}

function AdminNotificationSettings({ settings, save }) {
  const [s, setS] = useState({ ...settings })
  const set = (key, value) => setS(old => ({ ...old, [key]: value }))

  return <div className="card adminSection">
    <h2>Notifications & general</h2>
    <p className="muted">Control optional submit messages and whether users may add new driver names from their Settings page.</p>
    <div className="settingsGroups adminSettingsTwoCol">
      <SettingsGroup title="Submit notifications shown to users">
        <Rule label="Monthly milestones" enabled={s.milestoneNotificationsEnabled} setEnabled={v => set('milestoneNotificationsEnabled', v)} value={s.monthlyMilestoneStep} setValue={v => set('monthlyMilestoneStep', v)} suffix="pallet step" />
        <SimpleRule label="Monthly leaderboard / leader messages" enabled={s.leaderboardNotificationsEnabled} setEnabled={v => set('leaderboardNotificationsEnabled', v)} />
        <SimpleRule label="Current monthly balance message" enabled={s.balanceNotificationsEnabled} setEnabled={v => set('balanceNotificationsEnabled', v)} />
      </SettingsGroup>

      <SettingsGroup title="Driver-name access">
        <SimpleRule label="Allow users to add driver names from Settings" enabled={s.allowUsersAddDrivers} setEnabled={v => set('allowUsersAddDrivers', v)} />
      </SettingsGroup>
    </div>
    <button className="primary" onClick={() => save(s)}>Save notification & general settings</button>
  </div>
}

function AdminTabAccess({ users, save }) {
  return <div className="card adminSection">
    <h2>Tab access</h2>
    <p className="muted">Choose exactly which accounts can see and open the Statistics Driver and Daily Check tabs. Changes are enforced by the backend too, not only hidden from the menu. Logged-in users update automatically within about 15 seconds or when the browser regains focus.</p>
    <div className="tableWrap"><table className="tabAccessTable"><thead><tr><th>User</th><th>Role</th><th>Terminal</th><th>Statistics Driver</th><th>Daily Check</th><th></th></tr></thead><tbody>
    {users.map(user => <TabAccessRow key={user.id} user={user} save={save} />)}
    </tbody></table></div>
  </div>
}

function TabAccessRow({ user, save }) {
  const [driverStats, setDriverStats] = useState(user.showDriverStatisticsTab !== false)
  const [dailyCheck, setDailyCheck] = useState(user.showDailyCheckTab !== false)

  useEffect(() => {
    setDriverStats(user.showDriverStatisticsTab !== false)
    setDailyCheck(user.showDailyCheckTab !== false)
  }, [user.id, user.showDriverStatisticsTab, user.showDailyCheckTab])

  return <tr>
    <td><b>{user.username}</b><div className="muted small">{user.displayName}</div></td>
    <td>{user.role}</td>
    <td>{user.terminal}</td>
    <td><label className="miniCheck"><input type="checkbox" checked={driverStats} onChange={e => setDriverStats(e.target.checked)} /> Show</label></td>
    <td><label className="miniCheck"><input type="checkbox" checked={dailyCheck} onChange={e => setDailyCheck(e.target.checked)} /> Show</label></td>
    <td><button onClick={() => save(user, { showDriverStatisticsTab: driverStats, showDailyCheckTab: dailyCheck })}>Save</button></td>
  </tr>
}

function AdminDriverStatsSettings({ settings, save }) {
  const [s, setS] = useState({ ...settings, driverUnmatchedInDeduction: Number(settings.driverUnmatchedInDeduction ?? 15) })

  return <div className="card adminSection">
    <h2>Driver statistics</h2>
    <p className="muted">Adjusted driver statistics pair receipts per driver + day, regardless of vehicle. Every IN receipt without a matching OUT receipt deducts this number of pallets from the adjusted balance. Raw receipts and raw statistics are never changed.</p>
    <div className="settingsGroups adminSettingsTwoCol">
      <SettingsGroup title="Unmatched IN adjustment">
        <div className="ruleRow">
          <label><b>Deduction per unmatched IN receipt</b></label>
          <input className="smallNumber" type="number" min="0" max="5000" value={s.driverUnmatchedInDeduction} onChange={e => setS(old => ({ ...old, driverUnmatchedInDeduction: Number(e.target.value) }))} />
          <span>pallets</span>
        </div>
        <p className="muted small">Example with 15: 1 IN / 0 OUT = −15, 3 IN / 0 OUT = −45, 3 IN / 1 OUT = −30, 3 IN / 3 OUT = 0 deduction.</p>
      </SettingsGroup>
    </div>
    <button className="primary" onClick={() => save(s)}>Save driver statistics setting</button>
  </div>
}

function SettingsGroup({ title, children }) { return <div className="settingsGroup"><h3>{title}</h3>{children}</div> }
function SimpleRule({ label, enabled, setEnabled }) { return <div className="ruleRow"><label className="miniCheck"><input type="checkbox" checked={!!enabled} onChange={e => setEnabled(e.target.checked)} /> {label}</label></div> }
function Rule({ label, enabled, setEnabled, value, setValue, suffix, extra }) { return <div className="ruleRow"><label className="miniCheck"><input type="checkbox" checked={!!enabled} onChange={e => setEnabled(e.target.checked)} /> {label}</label><input className="smallNumber" type="number" min="1" value={value} onChange={e => setValue(Number(e.target.value))} /><span>{suffix}</span>{extra}</div> }

function Modal({ title, close, children }) { return <div className="modalBack" onMouseDown={e => { if (e.target === e.currentTarget) close() }}><div className="modal card"><div className="modalHead"><h2>{title}</h2><button onClick={close}>✕</button></div>{children}</div></div> }
function Empty({ text }) { return <div className="empty">{text}</div> }
function Loading() { return <div className="loading">Loading…</div> }

class AppErrorBoundary extends React.Component {
  constructor(props) {
    super(props)
    this.state = { error: null }
  }

  static getDerivedStateFromError(error) {
    return { error }
  }

  componentDidCatch(error, info) {
    console.error('PalletControl render error', error, info)
  }

  render() {
    if (!this.state.error) return this.props.children
    return <main className="shell"><div className="card"><h2>Page error</h2><p>The page hit a frontend rendering error. Your backend and saved data are not affected.</p><button className="primary" onClick={() => window.location.reload()}>Reload page</button></div></main>
  }
}

createRoot(document.getElementById('root')).render(<AppErrorBoundary><App /></AppErrorBoundary>)
