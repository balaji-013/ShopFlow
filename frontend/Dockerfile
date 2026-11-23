FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci                                  # exact versions from the lock file => reproducible builds
COPY . .
RUN npx ng build --configuration production

# nginx serves the static bundle AND reverse-proxies /api + /hubs to the API:
# browser sees ONE origin => no CORS, SameSite=Strict refresh cookie works, WebSockets are upgraded.
FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/shopflow-web/browser /usr/share/nginx/html
EXPOSE 80