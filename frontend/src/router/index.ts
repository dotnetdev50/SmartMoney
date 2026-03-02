import { createRouter, createWebHashHistory } from "vue-router";
import Dashboard from "@/pages/Dashboard.vue";

export default createRouter({
  history: createWebHashHistory(),
  routes: [{ path: "/", component: Dashboard }],
});
