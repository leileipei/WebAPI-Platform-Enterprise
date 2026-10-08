-- Private maintenance only. The caller must verify every owned Worker is stopped
-- and set the transaction-local guard. No transports or new attempts run here.
SELECT pg_advisory_xact_lock(8901202);
DO $$
BEGIN
  IF current_setting('webapi.notification_workers_stopped', true) IS DISTINCT FROM 'confirmed' THEN
    RAISE EXCEPTION 'notification maintenance requires stopped workers';
  END IF;
  PERFORM 1 FROM notification_deliveries WHERE status='Sending' ORDER BY id FOR UPDATE;
  IF EXISTS (
    SELECT 1 FROM notification_deliveries d WHERE d.status='Sending' AND NOT EXISTS (
      SELECT 1 FROM notification_delivery_attempts a WHERE a.delivery_id=d.id
        AND a.attempt_no=d.attempt_count AND a.lease_token=d.lease_token AND a.completed_at IS NULL)) THEN
    RAISE EXCEPTION 'notification maintenance found inconsistent attempt';
  END IF;
END $$;
UPDATE notification_delivery_attempts a
SET completed_at=clock_timestamp(), outcome='OutcomeUnknown', code='MaintenanceInterrupted', protocol_status=NULL
FROM notification_deliveries d
WHERE d.status='Sending' AND a.delivery_id=d.id AND a.attempt_no=d.attempt_count AND a.lease_token=d.lease_token AND a.completed_at IS NULL;
WITH fenced AS (
  UPDATE notification_deliveries SET
    status='Failed', reason='MaintenanceInterrupted', completed_at=clock_timestamp(),
    lease_owner=NULL, lease_until=NULL, lease_token=lease_token+1, revision=revision+1,
    next_attempt_at=clock_timestamp()+make_interval(secs => LEAST(max_delay_seconds::double precision,base_delay_seconds*power(2,attempt_count-1)*1.2))
  WHERE status='Sending'
  RETURNING id,organization_id,project_id,environment_id,channel,status,attempt_count
)
INSERT INTO audit_logs(organization_id,project_id,environment_id,action,resource_type,resource_id,after_json,trace_id,created_at)
SELECT organization_id,project_id,environment_id,'notification.delivery','NotificationDelivery',id::text,
  jsonb_build_object('channel',channel,'status',status,'attemptCount',attempt_count,'reason','MaintenanceInterrupted'),
  'notification:'||replace(id::text,'-',''),clock_timestamp() FROM fenced;
