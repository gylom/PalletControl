import React, { useEffect, useMemo, useState } from 'react'

const LABELS = {
  PENDING: 'Pending', SENDING: 'Sending', RETRYING: 'Retry scheduled',
  FAILED: 'Failed', SENT: 'Accepted by SMTP', SKIPPED: 'Skipped',
  MANUAL_RETRY: 'Manually requeued'
}

function dateTime(iso) {
  if (!iso || String(iso).startsWith('0001-')) return '—'
  const value = String(iso)
  const parsed = new Date(/(?:Z|[+-]\d\d:\d\d)$/i.test(value) ? value : `${value}Z`)
  return Number.isNaN(parsed.valueOf()) ? value : parsed.toLocaleString('nb-NO', {
    timeZone: 'Europe/Oslo', year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit'
  })
}

export default function EmailDeliveryLog({ api, data, targetTerminalId }) {
  const [content, setContent] = useState(data)
  const [filter, setFilter] = useState('ALL')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  useEffect(() => { setContent(data); setFilter('ALL'); setError(''); setNotice('') }, [data, targetTerminalId])
  async function refresh() {
    setBusy(true)
    try { setContent(await api(`/admin/email-log?terminalId=${targetTerminalId}`)); setError('') }
    catch (ex) { setError(ex.message || String(ex)) }
    finally { setBusy(false) }
  }
  useEffect(() => {
    let active = true
    const timer = setInterval(() => {
      if (!active || document.visibilityState !== 'visible') return
      api(`/admin/email-log?terminalId=${targetTerminalId}`)
        .then(result => { if (active) setContent(result) })
        .catch(() => {})
    }, 20000)
    return () => { active = false; clearInterval(timer) }
  }, [api, targetTerminalId])
  const messages = content?.messages || []
  const recent = content?.recent || []
  const shown = useMemo(() => filter === 'ALL' ? messages : messages.filter(m => m.status === filter), [messages, filter])
  async function retry(id) {
    setBusy(true); setNotice(''); setError('')
    try {
      await api(`/admin/email-log/${id}/retry`, {method:'POST', body:'{}'})
      setNotice('Queued for another attempt. Refresh to see the updated status.')
      setContent(await api(`/admin/email-log?terminalId=${targetTerminalId}`))
    } catch (ex) { setError(ex.message || String(ex)) }
    finally { setBusy(false) }
  }
  return <div className="card" style={{padding:20,maxWidth:1200}}>
    <h2>Email delivery log</h2>
    <p className="muted">Notifications for the selected terminal. “Accepted by SMTP” means the SMTP server accepted the message; it does not confirm delivery to an inbox. Refreshed automatically every 20 seconds.</p>
    {error && <div className="error">{error}</div>}
    {notice && <div className="success">{notice}</div>}
    <div style={{display:'flex',flexWrap:'wrap',alignItems:'center',gap:12,marginBottom:14}}>
      <label>Status <select value={filter} onChange={e=>setFilter(e.target.value)}>
        <option value="ALL">All statuses</option>
        {['PENDING','SENDING','RETRYING','FAILED','SENT','SKIPPED'].map(s => <option key={s} value={s}>{LABELS[s]}</option>)}
      </select></label>
      <button disabled={busy} onClick={refresh}>{busy ? 'Refreshing…' : 'Refresh now'}</button>
      <span className="muted">Showing {shown.length} of {messages.length} recent notifications</span>
    </div>
    <div style={{overflowX:'auto'}}>
      <table style={{width:'100%',borderCollapse:'collapse',minWidth:850,fontSize:13}}>
        <thead><tr>{['Control number','Status','Attempts','Recipient / CC','Created','Last attempt / next retry','Error','Action'].map(x=><th key={x} style={{textAlign:'left',padding:9,borderBottom:'1px solid #aaa'}}>{x}</th>)}</tr></thead>
        <tbody>{shown.map(m=><tr key={m.id}>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}>{m.controlNumber || m.entryId}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}><b>{LABELS[m.status]||m.status}</b></td>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}>{m.attempts}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773',overflowWrap:'anywhere'}}>{m.recipient||'Not prepared'}{m.cc&&<small style={{display:'block'}}>CC: {m.cc}</small>}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}>{dateTime(m.createdAtUtc)}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}>{dateTime(m.lastAttemptUtc)}{['PENDING','RETRYING','SENDING'].includes(m.status)&&<small style={{display:'block'}}>Next: {dateTime(m.nextAttemptUtc)}</small>}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773',maxWidth:220,overflowWrap:'anywhere'}}>{m.lastError||'—'}</td>
          <td style={{padding:9,borderBottom:'1px solid #7773'}}>{['FAILED','RETRYING'].includes(m.status)&&<button disabled={busy} onClick={()=>retry(m.id)}>Retry</button>}</td>
        </tr>)}</tbody>
      </table>
      {shown.length===0&&<p className="muted">No email notifications for this filter.</p>}
    </div>
    <h3 style={{marginTop:26}}>Recent delivery events and test emails</h3>
    <p className="muted">Includes individual failed attempts, successful SMTP acceptance, manual retries and test messages (SuperAdmin).</p>
    <div style={{overflowX:'auto'}}><table style={{width:'100%',borderCollapse:'collapse',minWidth:750,fontSize:13}}>
      <thead><tr>{['Time','Type','Outcome','Recipient','Details'].map(x=><th key={x} style={{textAlign:'left',padding:9,borderBottom:'1px solid #aaa'}}>{x}</th>)}</tr></thead>
      <tbody>{recent.map(e=><tr key={e.id}>
        <td style={{padding:9,borderBottom:'1px solid #7773'}}>{dateTime(e.createdAtUtc)}</td>
        <td style={{padding:9,borderBottom:'1px solid #7773'}}>{e.kind==='TEST'?'Test email':'Notification'}</td>
        <td style={{padding:9,borderBottom:'1px solid #7773'}}>{LABELS[e.outcome]||e.outcome}</td>
        <td style={{padding:9,borderBottom:'1px solid #7773',overflowWrap:'anywhere'}}>{e.recipient||'—'}</td>
        <td style={{padding:9,borderBottom:'1px solid #7773',overflowWrap:'anywhere'}}>{e.detail||'—'}</td>
      </tr>)}</tbody></table>{recent.length===0&&<p className="muted">No delivery events recorded yet.</p>}</div>
  </div>
}
