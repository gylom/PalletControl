import React, { useEffect, useMemo, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

const API = '/api'

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

function App() {
  const [me, setMe] = useState(() => {
    try { return JSON.parse(localStorage.getItem('me')) } catch { return null }
  })

  if (!me) return <Login onLogin={setMe} />

  return <Shell me={me} logout={() => {
    localStorage.removeItem('token')
    localStorage.removeItem('me')
    setMe(null)
  }} />
}

function Login({ onLogin }) {
  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('admin123')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  async function submit(e) {
    e.preventDefault()
    setBusy(true); setError('')
    try {
      const result = await api('/auth/login', {
        method: 'POST', body: JSON.stringify({ username, password })
      })
      localStorage.setItem('token', result.token)
      localStorage.setItem('me', JSON.stringify(result))
      onLogin(result)
    } catch (e) {
      setError(`Login failed: ${e.message}`)
    } finally { setBusy(false) }
  }

  return <div className="loginWrap">
    <form className="card login" onSubmit={submit}>
      <div className="brand">📦</div>
      <h1>Pallet Control</h1>
      <p className="muted">Sign in to your terminal</p>
      <label>Username<input value={username} onChange={e => setUsername(e.target.value)} /></label>
      <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} /></label>
      {error && <div className="error">{error}</div>}
      <button className="primary big" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      <div className="demo">Demo: admin/admin123 · super/super123 · user/user123</div>
    </form>
  </div>
}

function Shell({ me, logout }) {
  const [tab, setTab] = useState('register')
  const elevated = me.role === 'Admin' || me.role === 'Superuser'

  return <div>
    <header>
      <div className="headerBrand"><b>📦 Pallet Control</b><span className="terminal">{me.terminalCode}</span></div>
      <div className="userline">{me.displayName} · {me.role}<button className="linkbtn" onClick={logout}>Log out</button></div>
    </header>

    <nav>
      <NavButton id="register" tab={tab} setTab={setTab}>Register</NavButton>
      <NavButton id="stats" tab={tab} setTab={setTab}>Statistics</NavButton>
      <NavButton id="receipts" tab={tab} setTab={setTab}>Receipts</NavButton>
      {elevated && <NavButton id="warnings" tab={tab} setTab={setTab}>Warnings</NavButton>}
      {elevated && <NavButton id="export" tab={tab} setTab={setTab}>Export</NavButton>}
      <NavButton id="settings" tab={tab} setTab={setTab}>Settings</NavButton>
      {me.role === 'Admin' && <NavButton id="admin" tab={tab} setTab={setTab}>Admin</NavButton>}
    </nav>

    <div className="healthBarWrap"><HealthCheck /></div>

    <main>
      {tab === 'register' && <Register me={me} />}
      {tab === 'stats' && <Stats />}
      {tab === 'receipts' && <Receipts me={me} />}
      {tab === 'warnings' && elevated && <Warnings />}
      {tab === 'export' && elevated && <Export />}
      {tab === 'settings' && <UserSettings me={me} />}
      {tab === 'admin' && me.role === 'Admin' && <Admin />}
    </main>
  </div>
}

function NavButton({ id, tab, setTab, children }) {
  return <button className={tab === id ? 'active' : ''} onClick={() => setTab(id)}>{children}</button>
}

function HealthCheck() {
  const [health, setHealth] = useState({ api: 'checking', database: 'checking', overall: 'checking', checked: null })

  async function check() {
    try {
      const res = await fetch('/api/health', { cache: 'no-store' })
      const body = await res.json().catch(() => ({}))
      setHealth({
        api: 'online',
        database: body?.database?.status === 'online' ? 'online' : 'offline',
        overall: res.ok && body?.status === 'healthy' ? 'healthy' : 'unhealthy',
        checked: new Date()
      })
    } catch {
      setHealth({ api: 'offline', database: 'unknown', overall: 'unhealthy', checked: new Date() })
    }
  }

  useEffect(() => {
    check()
    const timer = setInterval(check, 15000)
    return () => clearInterval(timer)
  }, [])

  return <div className={`healthCheck ${health.overall}`} title="Health Check refreshes every 15 seconds">
    <b>Health Check</b>
    <HealthDot label="API" value={health.api} />
    <HealthDot label="Database" value={health.database} />
    <span className="healthTime">{health.checked ? health.checked.toLocaleTimeString('nb-NO', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : 'checking…'}</span>
    <button className="tiny" onClick={check}>↻</button>
  </div>
}

function HealthDot({ label, value }) {
  return <span className="healthItem"><span className={`dot ${value}`}></span>{label}: {value}</span>
}

function Register({ me }) {
  const elevated = me.role === 'Admin' || me.role === 'Superuser'
  const [data, setData] = useState(null)
  const [vehicle, setVehicle] = useState('')
  const [driver, setDriver] = useState('')
  const [driverOptions, setDriverOptions] = useState([])
  const [direction, setDirection] = useState('IN')
  const [qty, setQty] = useState({})
  const [newDriver, setNewDriver] = useState('')
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

  async function addDriver() {
    if (!newDriver.trim()) return
    setError('')
    try {
      const added = await api('/drivers/quick-add', {
        method: 'POST',
        body: JSON.stringify({ name: newDriver })
      })
      setNewDriver('')
      const setup = await load()
      if (vehicle) {
        setDriverOptions(await api(`/drivers/for-vehicle/${vehicle}`))
      } else {
        setDriverOptions(setup.drivers)
      }
      setDriver(String(added.id))
    } catch (e) {
      setError(e.message)
    }
  }

  function validate() {
    if (!vehicle) return 'Choose a vehicle.'
    if (!driver) return 'Choose a driver.'
    if (elevated && !businessDate) return 'Choose a receipt date.'
    if (!Object.values(qty).some(value => Number(value) > 0)) return 'Enter at least one pallet quantity.'
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
      .map(p => ({ palletTypeId: Number(p.id), quantity: Number(qty[p.id] || 0) }))
      .filter(x => x.quantity > 0)

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

        {data.allowUsersAddDrivers && (
          <div className="addrow registerAddRow">
            <input placeholder="Add new driver" value={newDriver} onChange={e => setNewDriver(e.target.value)} />
            <button type="button" onClick={addDriver}>Add</button>
          </div>
        )}

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

function Stats() {
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

  async function loadStats() {
    const p = new URLSearchParams({ from, to, sortBy })
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
    <div className="pageTitle"><div><h1>Statistics</h1><p>Filter pallet movements and compare driver performance.</p></div>
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
        <MultiSelect label="Transporter" options={options.transporters} selected={transporterIds} setSelected={ids => { setTransporterIds(ids); if (ids.length) { const allowed = new Set(options.vehicles.filter(v => ids.map(Number).includes(Number(v.transporterId))).map(v => Number(v.id))); setVehicleIds(old => old.filter(id => allowed.has(Number(id)))) } }} labelKey="name" />
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
  const elevated = me.role === 'Admin' || me.role === 'Superuser'
  const [date, setDate] = useState(dateInput())
  const [limit, setLimit] = useState('25')
  const [sort, setSort] = useState('desc')
  const [statusFilter, setStatusFilter] = useState('all')
  const [search, setSearch] = useState('')
  const [rows, setRows] = useState([])
  const [error, setError] = useState('')
  const [detail, setDetail] = useState(null)

  async function load(nextLimit = limit, nextSort = sort, nextDate = date, nextStatus = statusFilter, nextSearch = search) {
    const p = new URLSearchParams()

    if (elevated) {
      p.set('date', nextDate)
      p.set('sort', nextSort)
      p.set('limit', nextLimit === 'all' ? '0' : nextLimit)
      p.set('status', nextStatus)
      if (nextSearch.trim()) p.set('search', nextSearch.trim())
    } else {
      p.set('limit', '25')
      p.set('sort', 'desc')
    }

    const result = await api(`/receipts?${p}`)
    setRows(result.receipts || [])
  }

  useEffect(() => { load().catch(e => setError(e.message)) }, [])

  async function changeDate(value) { setDate(value); try { await load(limit, sort, value, statusFilter, search) } catch (e) { setError(e.message) } }
  async function changeLimit(value) { setLimit(value); try { await load(value, sort, date, statusFilter, search) } catch (e) { setError(e.message) } }
  async function changeSort(value) { setSort(value); try { await load(limit, value, date, statusFilter, search) } catch (e) { setError(e.message) } }
  async function changeStatus(value) { setStatusFilter(value); try { await load(limit, sort, date, value, search) } catch (e) { setError(e.message) } }

  async function submitSearch(e) {
    e.preventDefault()
    setError('')
    try { await load(limit, sort, date, statusFilter, search) }
    catch (e) { setError(e.message) }
  }

  async function clearSearch() {
    setSearch('')
    setError('')
    try { await load(limit, sort, date, statusFilter, '') }
    catch (e) { setError(e.message) }
  }

  async function cancel(r) {
    const reason = window.prompt(`Why cancel ${r.receiptNumber}?`)
    if (!reason?.trim()) return
    try { await api(`/receipts/${r.id}/cancel`, { method: 'POST', body: JSON.stringify({ reason }) }); await load(); setDetail(null) }
    catch (e) { setError(e.message) }
  }

  async function reverse(r) {
    const reason = window.prompt(`Reason for reversing cancellation of ${r.receiptNumber}?`, 'Cancellation reversed')
    if (reason === null) return
    try { await api(`/receipts/${r.id}/reverse-cancellation`, { method: 'POST', body: JSON.stringify({ reason }) }); await load(); setDetail(null) }
    catch (e) { setError(e.message) }
  }

  return <section>
    <div className="pageTitle"><div><h1>Receipts</h1><p>{elevated ? 'Default 25. Filter active, cancelled or previously reversed receipts for the selected date.' : 'Latest 25 receipts.'}</p></div></div>
    {error && <div className="error">{error}</div>}

    {elevated && <div className="card receiptControls">
      <label>Date<input type="date" value={date} onChange={e => changeDate(e.target.value)} /></label>
      <label>Time order<select value={sort} onChange={e => changeSort(e.target.value)}><option value="desc">Newest first</option><option value="asc">Oldest first</option></select></label>
      <div className="receiptStatusFilters"><span>Status:</span>{[
        ['all', 'All'], ['active', 'Active'], ['cancelled', 'Cancelled'], ['reversed', 'Reversed']
      ].map(([value, label]) => <button key={value} className={statusFilter === value ? 'active' : ''} onClick={() => changeStatus(value)}>{label}</button>)}</div>
      <div className="limitButtons"><span>Show:</span>{['25', '50', 'all'].map(v => <button key={v} className={limit === v ? 'active' : ''} onClick={() => changeLimit(v)}>{v === 'all' ? 'All' : v}</button>)}</div>
      <form className="receiptSearch" onSubmit={submitSearch}>
        <label><span>Search receipts</span><input value={search} onChange={e => setSearch(e.target.value)} placeholder="Receipt, vehicle, driver, transporter, pallet…" /></label>
        <button type="submit">Search</button>
        {search && <button type="button" className="smallLink" onClick={clearSearch}>Clear</button>}
      </form>
    </div>}

    <div className="receiptList">
      {rows.length === 0 && <Empty text={elevated ? (search ? 'No receipts match your search and filters.' : 'No receipts match these filters.') : 'No receipts yet.'} />}
      {rows.map(r => <div className={`receiptCard ${r.status === 'CANCELLED' ? 'cancelled' : ''}`} key={r.id}>
        <div className="receiptTop"><div><b>{r.receiptNumber}</b><span className={`badge ${r.status === 'CANCELLED' ? 'red' : 'green'}`}>{r.status}</span>{r.wasReversed && <span className="badge blue">REVERSED</span>}</div><strong className="clock">🕒 {formatTimestamp(r.submittedAtUtc)}</strong></div>
        <div className="receiptInfo">
          <span><small>Transporter</small>{r.transporter}</span>
          <span><small>Vehicle</small><b>{r.vehicle}</b></span>
          <span><small>Driver</small>{r.driver}</span>
          <span><small>Direction</small><b>{r.direction}</b></span>
          <span className="receiptAmounts"><small>Pallets</small><b>{r.items.map(i => `${i.quantity} ${i.palletType}`).join(', ') || '—'}</b></span>
        </div>
        <div className="receiptActions">
          {(r.status === 'CANCELLED' || r.wasReversed) && <button className="infoBtn" onClick={() => setDetail(r)}>ⓘ Receipt history</button>}
          {elevated && r.status === 'ACTIVE' && <button className="dangerGhost" onClick={() => cancel(r)}>Cancel</button>}
          {elevated && r.status === 'CANCELLED' && <button onClick={() => reverse(r)}>↶ Reverse cancellation</button>}
        </div>
      </div>)}
    </div>

    {detail && <Modal title={`Receipt history · ${detail.receiptNumber}`} close={() => setDetail(null)}>
      <div className="detailGrid"><span><small>Status</small><b>{detail.status}</b></span><span><small>Receipt date</small>{detail.businessDate}</span><span><small>Current reason</small>{detail.cancelReason || '—'}</span><span><small>Cancelled at</small>{formatTimestamp(detail.cancelledAtUtc) || '—'}</span></div>
      <h3>Audit history</h3>
      {detail.actions?.length ? detail.actions.map(a => <div className="historyRow" key={a.id}><b>{a.action.replaceAll('_', ' ')}</b><span>{a.user}</span><span>{formatTimestamp(a.createdAtUtc)}</span><p>{a.reason}</p></div>) : <Empty text="No receipt history." />}
      {elevated && detail.status === 'CANCELLED' && <button className="primary" onClick={() => reverse(detail)}>Reverse cancellation</button>}
    </Modal>}
  </section>
}

function Warnings() {
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

  return <section>
    <div className="pageTitle"><div><h1>Warnings</h1><p>Visible to Superusers and Admins. Only Admin can configure warning rules.</p></div><div className="warningCount">{data.openCount} open</div></div>
    {error && <div className="error">{error}</div>}
    <div className="warningToolbar">
      <div className="segmented warningTabs"><button className={onlyOpen ? 'active' : ''} onClick={() => setOnlyOpen(true)}>Open warnings</button><button className={!onlyOpen ? 'active' : ''} onClick={() => setOnlyOpen(false)}>All warnings</button></div>
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

function UserSettings({ me }) {
  const [s, setS] = useState(null)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  useEffect(() => { api('/me/settings').then(setS).catch(e => setError(e.message)) }, [])
  if (!s) return <Loading />

  async function save() {
    setMessage(''); setError('')
    try {
      const result = await api('/me/settings', { method: 'PUT', body: JSON.stringify(s) })
      setS(result); setMessage('Settings saved.')
    } catch (e) { setError(e.message) }
  }

  return <section><div className="pageTitle"><div><h1>My settings</h1><p>Personal notification preferences for {me.displayName}.</p></div></div>
    {message && <div className="success">{message}</div>}{error && <div className="error">{error}</div>}
    <div className="card settingsCard">
      <Toggle label="Monthly milestone notifications" text="Example: You have now brought in over 100 pallets this month." checked={s.showMilestoneNotifications} onChange={v => setS({ ...s, showMilestoneNotifications: v })} />
      <Toggle label="Leaderboard notifications" text="Show current monthly rank and who is ahead/behind after submission." checked={s.showLeaderboardNotifications} onChange={v => setS({ ...s, showLeaderboardNotifications: v })} />
      <Toggle label="Monthly balance notification" text="Show your selected driver's current IN, OUT and balance after submission." checked={s.showBalanceNotifications} onChange={v => setS({ ...s, showBalanceNotifications: v })} />
      <button className="primary" onClick={save}>Save my settings</button>
    </div>
    {me.role === 'Superuser' && <div className="card noteCard"><b>Superuser access</b><p>You can view/acknowledge warnings, manage cancellations and export data. Warning thresholds remain Admin-only.</p></div>}
  </section>
}

function Toggle({ label, text, checked, onChange }) {
  return <label className="toggleRow"><div><b>{label}</b><span>{text}</span></div><input type="checkbox" checked={!!checked} onChange={e => onChange(e.target.checked)} /></label>
}

function Export() {
  const initial = periodDates('thisMonth')
  const [from, setFrom] = useState(initial.from)
  const [to, setTo] = useState(initial.to)
  const [error, setError] = useState('')

  async function download() {
    setError('')
    try {
      const token = localStorage.getItem('token')
      const res = await fetch(`${API}/export?from=${from}&to=${to}`, { headers: { Authorization: `Bearer ${token}` } })
      if (!res.ok) throw new Error(`Export failed (${res.status})`)
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a'); a.href = url; a.download = `PalletExport_${from}_${to}.csv`; a.click(); URL.revokeObjectURL(url)
    } catch (e) { setError(e.message) }
  }

  return <section><div className="pageTitle"><div><h1>Export</h1><p>CSV export for Admin and Superuser.</p></div></div>{error && <div className="error">{error}</div>}
    <div className="card exportCard"><label>From<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label><label>To<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label><button className="primary" onClick={download}>Download CSV</button></div>
  </section>
}

function Admin() {
  const [category, setCategory] = useState('')
  const [data, setData] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [transporterName, setTransporterName] = useState('')
  const [vehicleForm, setVehicleForm] = useState({ vehicleId: '', terminalId: '', transporterId: '' })
  const [driverForm, setDriverForm] = useState({ name: '', terminalId: '' })
  const [userForm, setUserForm] = useState({ username: '', displayName: '', password: '', role: 'User', terminalId: '' })
  const [palletName, setPalletName] = useState('')
  // Each category load gets a sequence number. If the user jumps to another
  // category before the previous request finishes, the stale response is ignored.
  const adminLoadSequence = useRef(0)
  // Keep the selected category in a ref as well as state. Async requests/actions can
  // then verify that they still belong to the category currently on screen.
  const activeCategoryRef = useRef('')

  const categories = [
    { id: 'users', icon: '👤', title: 'Users', description: 'Accounts, roles, terminals and passwords.' },
    { id: 'vehicles', icon: '🚚', title: 'Vehicles', description: 'Vehicles and transporter assignments.' },
    { id: 'drivers', icon: '🪪', title: 'Driver names', description: 'Add or remove selectable driver names.' },
    { id: 'transporters', icon: '🏢', title: 'Transporters', description: 'Manage transport companies.' },
    { id: 'pallets', icon: '📦', title: 'Pallet types', description: 'Available pallet types and user visibility.' },
    { id: 'warnings', icon: '⚠️', title: 'Warning rules', description: 'Thresholds, duplicates and activity warnings.' },
    { id: 'notifications', icon: '🔔', title: 'Notifications & general', description: 'Submit messages and general user options.' }
  ]

  function endpointFor(cat) {
    return {
      users: '/admin/users',
      vehicles: '/admin/vehicles',
      drivers: '/admin/drivers',
      transporters: '/admin/transporters',
      pallets: '/admin/pallet-types',
      warnings: '/admin/settings',
      notifications: '/admin/settings'
    }[cat]
  }

  async function load(cat = activeCategoryRef.current) {
    if (!cat) return
    const requestSequence = ++adminLoadSequence.current
    setLoading(true)
    setError('')
    try {
      const result = await api(endpointFor(cat))

      // The user may already have selected another category. Never put an old
      // category response into the currently rendered category.
      if (requestSequence !== adminLoadSequence.current || activeCategoryRef.current !== cat) return

      const next = (cat === 'warnings' || cat === 'notifications') ? { settings: result } : result
      setData(next)

      if (cat === 'vehicles' && next.terminals?.[0] && !vehicleForm.terminalId) {
        setVehicleForm(f => ({
          ...f,
          terminalId: String(next.terminals[0].id),
          transporterId: String(next.transporters?.find(t => t.active)?.id || '')
        }))
      }
      if (cat === 'drivers' && next.terminals?.[0] && !driverForm.terminalId) {
        setDriverForm(f => ({ ...f, terminalId: String(next.terminals[0].id) }))
      }
      if (cat === 'users' && next.terminals?.[0] && !userForm.terminalId) {
        setUserForm(f => ({ ...f, terminalId: String(next.terminals[0].id) }))
      }
    } catch (e) {
      if (requestSequence === adminLoadSequence.current && activeCategoryRef.current === cat) throw e
    } finally {
      if (requestSequence === adminLoadSequence.current && activeCategoryRef.current === cat) setLoading(false)
    }
  }

  useEffect(() => {
    // Clear the previous category before the new one can render. This is what
    // prevents e.g. the Users view from trying to read a Vehicles response.
    activeCategoryRef.current = category
    setData(null)
    setMessage('')
    setError('')
    if (category) load(category).catch(e => {
      if (activeCategoryRef.current === category) setError(e.message)
    })
    else {
      adminLoadSequence.current += 1
      setLoading(false)
    }
  }, [category])

  async function action(fn, ok = 'Saved.') {
    const actionCategory = activeCategoryRef.current
    setError('')
    setMessage('')
    try {
      await fn()
      // If the user moved to another category while the save/delete request was
      // running, do not reload the old category into the new category's view.
      if (activeCategoryRef.current !== actionCategory) return
      await load(actionCategory)
      if (activeCategoryRef.current === actionCategory) setMessage(ok)
    } catch (e) {
      if (activeCategoryRef.current === actionCategory) setError(e.message)
    }
  }

  async function del(kind, row, display) {
    if (!window.confirm(`Delete ${display}? Historical receipts will keep their snapshot information.`)) return
    await action(() => api(`/admin/${kind}/${row.id}`, { method: 'DELETE' }), `${display} deleted.`)
  }

  function chooseCategory(id) {
    if (id === activeCategoryRef.current) return

    // Update the ref immediately, before React renders the next category. This
    // invalidates both in-flight loads and in-flight save/delete refreshes.
    activeCategoryRef.current = id
    adminLoadSequence.current += 1
    setData(null)
    setLoading(true)
    setMessage('')
    setError('')
    setCategory(id)
  }

  function closeCategory() {
    activeCategoryRef.current = ''
    adminLoadSequence.current += 1
    setData(null)
    setLoading(false)
    setMessage('')
    setError('')
    setCategory('')
  }

  const activeTransporters = data?.transporters?.filter(t => t.active) || []
  const normalizedVehicleId = vehicleForm.vehicleId.trim().toUpperCase()
  const vehicleAlreadyExists = Boolean(
    normalizedVehicleId && data?.vehicles?.some(v => v.vehicleId.toUpperCase() === normalizedVehicleId)
  )

  return <section>
    <div className="pageTitle">
      <div>
        <h1>Admin</h1>
        <p>Choose what you want to manage. Only the selected category is loaded.</p>
      </div>
      {category && <button onClick={closeCategory}>Close category</button>}
    </div>

    <div className="adminCategoryGrid">
      {categories.map(c => <button
        key={c.id}
        className={`adminCategoryCard ${category === c.id ? 'active' : ''}`}
        onClick={() => chooseCategory(c.id)}
      >
        <span className="adminCategoryIcon">{c.icon}</span>
        <span className="adminCategoryText"><b>{c.title}</b><small>{c.description}</small></span>
        <span className="adminCategoryArrow">›</span>
      </button>)}
    </div>

    {message && <div className="success adminFeedback">{message}</div>}
    {error && <div className="error adminFeedback">{error}</div>}

    {!category && <div className="card adminWelcome">
      <b>Select an Admin category above</b>
      <p className="muted">Nothing else is loaded until you choose a category.</p>
    </div>}

    {category && loading && <Loading />}

    {category && !loading && data && <div className="adminCategoryContent">
      {category === 'transporters' && <AdminSection title="Transporters" subtitle="Deleting a transporter unassigns its vehicles; old receipts keep the transporter snapshot.">
        <div className="inlineForm"><input placeholder="Transporter name" value={transporterName} onChange={e => setTransporterName(e.target.value)} /><button className="primary" onClick={() => action(async () => { await api('/admin/transporters', { method: 'POST', body: JSON.stringify({ name: transporterName }) }); setTransporterName('') }, 'Transporter added.')}>Add transporter</button></div>
        <div className="adminRows">{data.transporters.map(t => <div className="adminRow" key={t.id}><b>{t.name}</b><span>{t.active ? 'Active' : 'Inactive'}</span><button className="dangerGhost" onClick={() => del('transporters', t, t.name)}>Delete</button></div>)}</div>
      </AdminSection>}

      {category === 'vehicles' && <AdminSection title="Vehicles" subtitle="Every vehicle can be tied to a transporter. Delete removes it from future selection while receipt snapshots remain.">
        <div className="inlineForm three"><input placeholder="Vehicle ID" value={vehicleForm.vehicleId} onChange={e => setVehicleForm({ ...vehicleForm, vehicleId: e.target.value })} /><select value={vehicleForm.terminalId} onChange={e => setVehicleForm({ ...vehicleForm, terminalId: e.target.value })}>{data.terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select><select value={vehicleForm.transporterId} onChange={e => setVehicleForm({ ...vehicleForm, transporterId: e.target.value })}><option value="">Choose transporter</option>{activeTransporters.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select><button className="primary" disabled={!normalizedVehicleId || !vehicleForm.terminalId || !vehicleForm.transporterId || vehicleAlreadyExists} onClick={() => action(async () => { await api('/admin/vehicles', { method: 'POST', body: JSON.stringify({ vehicleId: normalizedVehicleId, terminalId: Number(vehicleForm.terminalId), transporterId: Number(vehicleForm.transporterId) }) }); setVehicleForm({ ...vehicleForm, vehicleId: '' }) }, 'Vehicle added.')}>Add vehicle</button></div>
        {vehicleAlreadyExists && <div className="warningInline">Vehicle {normalizedVehicleId} already exists.</div>}
        <div className="adminRows">{data.vehicles.map(v => <div className="adminRow vehicleAdmin" key={v.id}><b>{v.vehicleId}</b><span>{v.terminal}</span><select value={v.transporterId || ''} onChange={e => { if (e.target.value) action(() => api(`/admin/vehicles/${v.id}/transporter`, { method: 'PUT', body: JSON.stringify({ transporterId: Number(e.target.value) }) }), 'Transporter changed.') }}><option value="">Not assigned</option>{data.transporters.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select><button className="dangerGhost" onClick={() => del('vehicles', v, v.vehicleId)}>Delete</button></div>)}</div>
      </AdminSection>}

      {category === 'drivers' && <AdminSection title="Driver names" subtitle="Delete removes a driver from future selection; historical receipts keep the driver snapshot.">
        <div className="inlineForm"><input placeholder="Driver name" value={driverForm.name} onChange={e => setDriverForm({ ...driverForm, name: e.target.value })} /><select value={driverForm.terminalId} onChange={e => setDriverForm({ ...driverForm, terminalId: e.target.value })}>{data.terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select><button className="primary" onClick={() => action(async () => { await api('/admin/drivers', { method: 'POST', body: JSON.stringify({ name: driverForm.name, terminalId: Number(driverForm.terminalId) }) }); setDriverForm({ ...driverForm, name: '' }) }, 'Driver added.')}>Add driver</button></div>
        <div className="adminRows">{data.drivers.map(d => <div className="adminRow" key={d.id}><b>{d.name}</b><span>{d.terminal}</span><button className="dangerGhost" onClick={() => del('drivers', d, d.name)}>Delete</button></div>)}</div>
      </AdminSection>}

      {category === 'pallets' && <AdminSection title="Pallet types">
        <div className="inlineForm"><input placeholder="New pallet type" value={palletName} onChange={e => setPalletName(e.target.value)} /><button className="primary" onClick={() => action(async () => { await api('/admin/pallet-types', { method: 'POST', body: JSON.stringify({ name: palletName, userSelectable: true }) }); setPalletName('') }, 'Pallet type added.')}>Add pallet type</button></div>
        <div className="adminRows">{data.palletTypes.map(p => <PalletAdminRow key={p.id} row={p} save={(next) => action(() => api(`/admin/pallet-types/${p.id}`, { method: 'PUT', body: JSON.stringify(next) }))} />)}</div>
      </AdminSection>}

      {category === 'users' && <AdminSection title="Users" subtitle="Create accounts or change display name, role, terminal, active status and password.">
        <div className="inlineForm userAdd"><input placeholder="Username" value={userForm.username} onChange={e => setUserForm({ ...userForm, username: e.target.value })} /><input placeholder="Display name" value={userForm.displayName} onChange={e => setUserForm({ ...userForm, displayName: e.target.value })} /><input type="password" placeholder="Password" value={userForm.password} onChange={e => setUserForm({ ...userForm, password: e.target.value })} /><select value={userForm.role} onChange={e => setUserForm({ ...userForm, role: e.target.value })}><option>User</option><option>Superuser</option><option>Admin</option></select><select value={userForm.terminalId} onChange={e => setUserForm({ ...userForm, terminalId: e.target.value })}>{data.terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select><button className="primary" onClick={() => action(async () => { await api('/admin/users', { method: 'POST', body: JSON.stringify({ ...userForm, terminalId: Number(userForm.terminalId) }) }); setUserForm({ ...userForm, username: '', displayName: '', password: '' }) }, 'User created.')}>Create user</button></div>
        <div className="adminRows">{data.users.map(u => <UserAdminRow key={u.id} row={u} terminals={data.terminals} save={(next) => action(() => api(`/admin/users/${u.id}`, { method: 'PUT', body: JSON.stringify(next) }), 'User updated.')} resetPassword={() => { const password = window.prompt(`New password for ${u.username}:`); if (password) action(() => api(`/admin/users/${u.id}/password`, { method: 'POST', body: JSON.stringify({ password }) }), 'Password changed.') }} />)}</div>
      </AdminSection>}

      {category === 'warnings' && <AdminWarningSettings settings={data.settings} save={next => action(() => api('/admin/settings', { method: 'PUT', body: JSON.stringify(next) }), 'Warning rules saved.')} />}

      {category === 'notifications' && <AdminNotificationSettings settings={data.settings} save={next => action(() => api('/admin/settings', { method: 'PUT', body: JSON.stringify(next) }), 'Notification and general settings saved.')} />}
    </div>}
  </section>
}

function AdminSection({ title, subtitle, children }) { return <div className="card adminSection"><h2>{title}</h2>{subtitle && <p className="muted">{subtitle}</p>}{children}</div> }

function PalletAdminRow({ row, save }) {
  const [active, setActive] = useState(row.active)
  const [selectable, setSelectable] = useState(row.userSelectable)
  return <div className="adminRow"><b>{row.name}</b><label className="miniCheck"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} /> Active</label><label className="miniCheck"><input type="checkbox" checked={selectable} onChange={e => setSelectable(e.target.checked)} /> User selectable</label><button onClick={() => save({ active, userSelectable: selectable })}>Save</button></div>
}

function UserAdminRow({ row, terminals, save, resetPassword }) {
  const [displayName, setDisplayName] = useState(row.displayName)
  const [role, setRole] = useState(row.role)
  const [terminalId, setTerminalId] = useState(String(row.terminalId))
  const [active, setActive] = useState(row.active)
  return <div className="adminRow userAdmin"><b>{row.username}</b><input value={displayName} onChange={e => setDisplayName(e.target.value)} /><select value={role} onChange={e => setRole(e.target.value)}><option>User</option><option>Superuser</option><option>Admin</option></select><select value={terminalId} onChange={e => setTerminalId(e.target.value)}>{terminals.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}</select><label className="miniCheck"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} /> Active</label><button onClick={() => save({ displayName, role, terminalId: Number(terminalId), active })}>Save</button><button onClick={resetPassword}>Password</button></div>
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
    <p className="muted">Control the optional messages users see after submitting and general registration options.</p>
    <div className="settingsGroups adminSettingsTwoCol">
      <SettingsGroup title="Submit notifications shown to users">
        <Rule label="Monthly milestones" enabled={s.milestoneNotificationsEnabled} setEnabled={v => set('milestoneNotificationsEnabled', v)} value={s.monthlyMilestoneStep} setValue={v => set('monthlyMilestoneStep', v)} suffix="pallet step" />
        <SimpleRule label="Monthly leaderboard / leader messages" enabled={s.leaderboardNotificationsEnabled} setEnabled={v => set('leaderboardNotificationsEnabled', v)} />
        <SimpleRule label="Current monthly balance message" enabled={s.balanceNotificationsEnabled} setEnabled={v => set('balanceNotificationsEnabled', v)} />
      </SettingsGroup>

      <SettingsGroup title="General registration settings">
        <SimpleRule label="Allow users to quick-add driver names" enabled={s.allowUsersAddDrivers} setEnabled={v => set('allowUsersAddDrivers', v)} />
      </SettingsGroup>
    </div>
    <button className="primary" onClick={() => save(s)}>Save notification & general settings</button>
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
