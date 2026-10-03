import {defineConfig} from 'vite';import react from '@vitejs/plugin-react';
export default defineConfig({plugins:[react()],server:{proxy:{'/api':{target:process.env.WEBAPI_CONTROL_PLANE_URL||'http://127.0.0.1:5090',changeOrigin:false}}}});
