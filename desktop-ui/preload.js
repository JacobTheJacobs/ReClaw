const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('clawDesktop', {
  getContext: () => ipcRenderer.invoke('app:get-context'),
  getGatewayStatus: () => ipcRenderer.invoke('app:get-gateway-status'),
  getGatewayAutostart: () => ipcRenderer.invoke('app:get-gateway-autostart'),
  ensureGatewayOnline: (options) => ipcRenderer.invoke('app:ensure-gateway-online', options || {}),
  pickArchive: () => ipcRenderer.invoke('app:pick-archive'),
  checkArchiveEncrypted: (archivePath) => ipcRenderer.invoke('app:check-archive-encrypted', archivePath),
  runAction: (payload) => ipcRenderer.invoke('app:run-action', payload),
  stopAction: () => ipcRenderer.invoke('app:stop-action'),
  // OpenClaw setup wizard
  checkOpenClaw: () => ipcRenderer.invoke('app:check-openclaw'),
  installOpenClaw: () => ipcRenderer.invoke('app:install-openclaw'),
  onboardOpenClaw: () => ipcRenderer.invoke('app:onboard-openclaw'),
  wizardRestore: (archivePath, password) => ipcRenderer.invoke('app:wizard-restore', { archivePath, password }),
  onLog: (handler) => {
    const wrapped = (_, data) => handler(data);
    ipcRenderer.on('app:log', wrapped);
    return () => ipcRenderer.removeListener('app:log', wrapped);
  },
  onStatus: (handler) => {
    const wrapped = (_, data) => handler(data);
    ipcRenderer.on('app:status', wrapped);
    return () => ipcRenderer.removeListener('app:status', wrapped);
  },
});
