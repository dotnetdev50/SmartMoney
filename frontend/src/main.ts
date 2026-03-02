import './assets/main.css'

import { createApp } from 'vue'
import App from './App.vue'
import router from './router'

const savedTheme = localStorage.getItem('theme')
const isDarkDefault = savedTheme ? savedTheme === 'dark' : true

document.documentElement.classList.toggle('dark', isDarkDefault)

createApp(App).use(router).mount('#app')
