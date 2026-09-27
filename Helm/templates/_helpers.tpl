{{- define "voting-app.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "voting-app.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name (include "voting-app.name" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{- define "voting-app.labels" -}}
app.kubernetes.io/name: {{ include "voting-app.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end }}

{{- define "voting-app.selectorLabels" -}}
app.kubernetes.io/name: {{ include "voting-app.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "voting-app.componentLabels" -}}
{{ include "voting-app.selectorLabels" .root }}
app.kubernetes.io/component: {{ .component }}
{{- end }}

{{- define "voting-app.redisServiceName" -}}
{{ include "voting-app.fullname" . }}-redis
{{- end }}

{{- define "voting-app.postgresServiceName" -}}
{{ include "voting-app.fullname" . }}-postgres
{{- end }}

{{- define "voting-app.postgresSecretName" -}}
{{- default (printf "%s-postgres" (include "voting-app.fullname" .)) .Values.postgres.auth.existingSecret }}
{{- end }}
