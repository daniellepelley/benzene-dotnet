{{/*
Standard helm-create-style helpers - naming/labels only, nothing mesh-specific lives here.
*/}}

{{- define "benzene-mesh.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "benzene-mesh.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "benzene-mesh.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "benzene-mesh.selectorLabels" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "benzene-mesh.selectorLabels" -}}
app.kubernetes.io/name: {{ include "benzene-mesh.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "benzene-mesh.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{ default (include "benzene-mesh.fullname" .) .Values.serviceAccount.name }}
{{- else -}}
{{ default "default" .Values.serviceAccount.name }}
{{- end -}}
{{- end -}}

{{- define "benzene-mesh.artifactMountPath" -}}
{{- default "/data/mesh-artifacts" .Values.meshConfig.artifactRootDirectory -}}
{{- end -}}
