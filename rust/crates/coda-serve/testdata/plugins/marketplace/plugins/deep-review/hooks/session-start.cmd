@echo off
rem SessionStart hook: proves it ran and that CODA_PLUGIN_ROOT was exported.
if defined CODA_PLUGIN_ROOT (set ROOTVAR=env-ok) else (set ROOTVAR=env-missing)
echo {"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"DEEP-REVIEW-HOOK-RAN %ROOTVAR%"}}
