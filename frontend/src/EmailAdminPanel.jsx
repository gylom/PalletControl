import React, { useEffect, useState } from 'react'

const EMAIL_TAGS = ['Terminal','ControlNumber','FromTerminal','Date','UnitReference',
  'ReceiptPalletCount','ActualPalletCount','Shortage','RegisteredBy','Comment']
const initialSmtp = { enabled:false, host:'', port:587, startTls:true, from:'', username:'', password:'', clearPassword:false }

export default function EmailAdminPanel({ api, data, superAdmin, targetTerminalId }) {
  const [terminal, setTerminal] = useState(data.settings)
  const [smtp, setSmtp] = useState(initialSmtp)
  const [smtpLoading, setSmtpLoading] = useState(false)
  const [toTest, setToTest] = useState('')
  const [ccTest, setCcTest] = useState('')
  const [busy, setBusy] = useState('')
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')

  useEffect(() => { setTerminal(data.settings); setNotice(''); setError('') }, [data, targetTerminalId])
  useEffect(() => {
    if (!superAdmin) return
    let alive = true
    setSmtpLoading(true)
    api('/admin/smtp-settings').then(r => { if (alive) setSmtp({...initialSmtp, ...r, password:''}) })
      .catch(e => { if (alive) setError(e.message) })
      .finally(() => { if (alive) setSmtpLoading(false) })
    return () => { alive = false }
  }, [superAdmin])

  async function run(label, action) {
    setError(''); setNotice(''); setBusy(label)
    try { await action(); setNotice(`${label} completed successfully.`) }
    catch (e) { setError(e.message || String(e)) }
    finally { setBusy('') }
  }
  const tChange = (field, value) => setTerminal(v => ({...v,[field]:value}))
  const sChange = (field, value) => setSmtp(v => ({...v,[field]:value}))

  return <div className="card" style={{padding:20,maxWidth:1100}}>
    <h2>Email notifications · {data.terminalCode}</h2>
    <p className="muted">Email is triggered when the number on the pallet receipt exceeds the pallets actually received. Each terminal has its own recipients and message.</p>
    {notice && <div className="success">{notice}</div>}
    {error && <div className="error">{error}</div>}

    <h3>Terminal email configuration</h3>
    <label style={{display:'block',marginBottom:14}}><input type="checkbox" checked={terminal.enabled} onChange={e=>tChange('enabled',e.target.checked)}/> Enable alerts for {data.terminalCode}</label>
    <div style={{display:'grid',gap:12,gridTemplateColumns:'repeat(auto-fit,minmax(250px,1fr))'}}>
      <label>To addresses (separate with semicolons)
        <input style={{width:'100%'}} value={terminal.to} onChange={e=>tChange('to',e.target.value)} placeholder="recipient@example.com"/>
      </label>
      <label>CC addresses (optional)
        <input style={{width:'100%'}} value={terminal.cc} onChange={e=>tChange('cc',e.target.value)} placeholder="manager@example.com"/>
      </label>
    </div>
    <label style={{display:'block',marginTop:12}}>Email subject
      <input style={{width:'100%'}} value={terminal.subject} onChange={e=>tChange('subject',e.target.value)}/>
    </label>
    <label style={{display:'block',marginTop:12}}>Email body
      <textarea style={{width:'100%',minHeight:230,fontFamily:'inherit',padding:10}} value={terminal.body} onChange={e=>tChange('body',e.target.value)}/>
    </label>
    <p className="muted">Available placeholders: {EMAIL_TAGS.map(v => `{${v}}`).join(', ')}</p>
    <button className="primary" disabled={!!busy} onClick={()=>run('Terminal settings save', async()=>{
      await api(`/admin/email-settings?terminalId=${targetTerminalId}`, {method:'PUT',body:JSON.stringify(terminal)})
    })}>Save {data.terminalCode} email settings</button>

    {superAdmin && <div style={{marginTop:30,paddingTop:18,borderTop:'1px solid #ddd'}}>
      <h3>Global SMTP configuration · SuperAdmin only</h3>
      {smtpLoading ? <p>Loading SMTP settings…</p> : <>
        <label style={{display:'block',marginBottom:12}}><input type="checkbox" checked={smtp.enabled} onChange={e=>sChange('enabled',e.target.checked)}/> Enable SMTP sending globally</label>
        <div style={{display:'grid',gap:12,gridTemplateColumns:'repeat(auto-fit,minmax(240px,1fr))'}}>
          <label>SMTP host<input style={{width:'100%'}} value={smtp.host} onChange={e=>sChange('host',e.target.value)} placeholder="smtp.example.com"/></label>
          <label>Port<input style={{width:'100%'}} type="number" min="1" max="65535" value={smtp.port} onChange={e=>sChange('port',Number(e.target.value))}/></label>
          <label>From address<input style={{width:'100%'}} value={smtp.from} onChange={e=>sChange('from',e.target.value)} placeholder="palletcontrol@example.com"/></label>
          <label>SMTP username (blank for relay)<input style={{width:'100%'}} autoComplete="off" value={smtp.username} onChange={e=>sChange('username',e.target.value)}/></label>
          <label>SMTP password {smtp.hasPassword?'(saved — leave blank to keep)':'(none saved)'}
            <input style={{width:'100%'}} type="password" autoComplete="new-password" value={smtp.password} onChange={e=>sChange('password',e.target.value)} placeholder="Leave blank to preserve existing"/>
          </label>
        </div>
        <label style={{display:'block',marginTop:12}}><input type="checkbox" checked={smtp.startTls} onChange={e=>sChange('startTls',e.target.checked)}/> STARTTLS / EnableSsl (typically port 587; implicit TLS on 465 is not supported)</label>
        <label style={{display:'block',marginTop:12}}><input type="checkbox" checked={smtp.clearPassword} onChange={e=>sChange('clearPassword',e.target.checked)}/> Remove saved SMTP password</label>
        <div style={{marginTop:14}}><button className="primary" disabled={!!busy} onClick={()=>run('SMTP settings save',async()=>{
          const saved=await api('/admin/smtp-settings',{method:'PUT',body:JSON.stringify(smtp)})
          setSmtp({...initialSmtp,...saved,password:''})
        })}>Save SMTP configuration</button></div>
        <h3 style={{marginTop:25}}>Send test email</h3>
        <div style={{display:'grid',gap:12,gridTemplateColumns:'repeat(auto-fit,minmax(240px,1fr))'}}>
          <label>Test recipient<input style={{width:'100%'}} value={toTest} onChange={e=>setToTest(e.target.value)} placeholder="me@example.com"/></label>
          <label>Test CC (optional)<input style={{width:'100%'}} value={ccTest} onChange={e=>setCcTest(e.target.value)}/></label>
        </div>
        <div style={{marginTop:14}}><button disabled={!!busy || !toTest.trim()} onClick={()=>run('Test email',()=>api('/admin/smtp-test',{method:'POST',body:JSON.stringify({to:toTest,cc:ccTest})}))}>Send test email</button></div>
      </>}
    </div>}
  </div>
}
